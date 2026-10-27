using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence.Authorization;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.UnitTests.Infrastructure;

public class EfPermissionDataSourceTests
{
    private readonly DbContextOptions<AppIdentityDbContext> _identityOptions =
        new DbContextOptionsBuilder<AppIdentityDbContext>().UseInMemoryDatabase("identity-" + Guid.NewGuid()).Options;

    private readonly DbContextOptions<AppAuthDbContext> _authOptions =
        new DbContextOptionsBuilder<AppAuthDbContext>().UseInMemoryDatabase("auth-" + Guid.NewGuid()).Options;

    private EfPermissionDataSource CreateSource()
        => new(new AppIdentityDbContext(_identityOptions), new AppAuthDbContext(_authOptions));

    private async Task SeedAsync(Action<AppIdentityDbContext> identity, Action<AppAuthDbContext> auth)
    {
        await using var identityContext = new AppIdentityDbContext(_identityOptions);
        identity(identityContext);
        await identityContext.SaveChangesAsync();

        await using var authContext = new AppAuthDbContext(_authOptions);
        auth(authContext);
        await authContext.SaveChangesAsync();
    }

    // ---- Users ----

    [Fact]
    public async Task GetUserAccessState_ReturnsActiveAndLockoutInfo()
    {
        var lockout = DateTimeOffset.UtcNow.AddHours(1);
        await SeedAsync(
            i => i.Users.AddRange(
                new ApplicationUser { Id = "u1", UserName = "a", IsActive = true },
                new ApplicationUser { Id = "u2", UserName = "b", IsActive = false, LockoutEnd = lockout }),
            _ => { });
        var source = CreateSource();

        Assert.Equal(new(true, null), await source.GetUserAccessStateAsync("u1"));
        Assert.Equal(new(false, lockout), await source.GetUserAccessStateAsync("u2"));
        Assert.Null(await source.GetUserAccessStateAsync("missing"));
    }

    [Fact]
    public async Task IsGlobalAdmin_IsTrueOnlyForMembersOfTheGlobalAdminRole()
    {
        await SeedAsync(
            i =>
            {
                i.Roles.AddRange(new ApplicationRole("GlobalAdmin") { Id = "r-admin" }, new ApplicationRole("Other") { Id = "r-other" });
                i.UserRoles.AddRange(
                    new IdentityUserRole<string> { UserId = "admin", RoleId = "r-admin" },
                    new IdentityUserRole<string> { UserId = "other", RoleId = "r-other" });
            },
            _ => { });
        var source = CreateSource();

        Assert.True(await source.IsGlobalAdminAsync("admin"));
        Assert.False(await source.IsGlobalAdminAsync("other"));
        Assert.False(await source.IsGlobalAdminAsync("nobody"));
    }

    // ---- Projects & membership ----

    [Fact]
    public async Task FindProject_PrefersTheNonDeletedProject()
    {
        var deleted = new Project("ERP", "Old ERP");
        deleted.MarkDeleted();
        var live = new Project("ERP", "New ERP");
        await SeedAsync(_ => { }, a => a.Projects.AddRange(deleted, live));

        var found = await CreateSource().FindProjectAsync("ERP");

        Assert.NotNull(found);
        Assert.Equal(live.Id, found.Id);
        Assert.False(found.IsDeleted);
        Assert.Null(await CreateSource().FindProjectAsync("CRM"));
    }

    [Fact]
    public async Task GetProjectMembership_ReturnsFlagsOfTheAssignment()
    {
        var project = new Project("ERP", "ERP");
        var admin = new ProjectUserAssignment(project.Id, "u1", isProjectAdmin: true);
        var blocked = new ProjectUserAssignment(project.Id, "u2");
        blocked.Block();
        await SeedAsync(_ => { }, a =>
        {
            a.Projects.Add(project);
            a.ProjectUserAssignments.AddRange(admin, blocked);
        });
        var source = CreateSource();

        Assert.Equal(new(true, false), await source.GetProjectMembershipAsync(project.Id, "u1"));
        Assert.Equal(new(false, true), await source.GetProjectMembershipAsync(project.Id, "u2"));
        Assert.Null(await source.GetProjectMembershipAsync(project.Id, "u3"));
        Assert.Null(await source.GetProjectMembershipAsync(Guid.NewGuid(), "u1"));
    }

    // ---- Permissions ----

    [Fact]
    public async Task GetAllPermissionCodes_ReturnsGlobalAndOwnProjectPermissions_Active_Only()
    {
        var erp = new Project("ERP", "ERP");
        var crm = new Project("CRM", "CRM");
        var inactive = new Permission("erp.old", "Old", "Erp", erp.Id);
        inactive.Deactivate();
        await SeedAsync(_ => { }, a =>
        {
            a.Projects.AddRange(erp, crm);
            a.Permissions.AddRange(
                new Permission("users.view", "Users", "Users"),
                new Permission("erp.orders.create", "Create", "Orders", erp.Id),
                new Permission("crm.leads.view", "Leads", "Leads", crm.Id),
                inactive);
        });

        var codes = await CreateSource().GetAllPermissionCodesAsync(erp.Id);

        Assert.Equal(new[] { "erp.orders.create", "users.view" }, codes.Order());
    }

    [Fact]
    public async Task GetRolePermissionCodes_FollowsUserRolesInTheProject_AndIgnoresForeignOrInactivePermissions()
    {
        var erp = new Project("ERP", "ERP");
        var crm = new Project("CRM", "CRM");
        var global = new Permission("users.view", "Users", "Users");
        var own = new Permission("erp.orders.create", "Create", "Orders", erp.Id);
        var foreign = new Permission("crm.leads.view", "Leads", "Leads", crm.Id);
        var inactive = new Permission("erp.orders.delete", "Delete", "Orders", erp.Id);
        inactive.Deactivate();

        await SeedAsync(_ => { }, a =>
        {
            a.Projects.AddRange(erp, crm);
            a.Permissions.AddRange(global, own, foreign, inactive);
            a.RolePermissions.AddRange(
                new RolePermission("manager", global.Id), new RolePermission("manager", own.Id),
                new RolePermission("manager", foreign.Id), new RolePermission("manager", inactive.Id),
                new RolePermission("viewer", own.Id));
            a.UserRoleProjectAssignments.AddRange(
                new UserRoleProjectAssignment("u1", "manager", erp.Id),
                new UserRoleProjectAssignment("u1", "viewer", erp.Id), // overlapping permission is returned once
                new UserRoleProjectAssignment("u1", "manager", crm.Id), // another project: must not leak into ERP
                new UserRoleProjectAssignment("u2", "manager", erp.Id));
        });
        var source = CreateSource();

        Assert.Equal(new[] { "erp.orders.create", "users.view" }, (await source.GetRolePermissionCodesAsync("u1", erp.Id)).Order());
        // In CRM the role exists too, but ERP's permission does not apply there.
        Assert.Equal(new[] { "crm.leads.view", "users.view" }, (await source.GetRolePermissionCodesAsync("u1", crm.Id)).Order());
        Assert.Empty(await source.GetRolePermissionCodesAsync("u3", erp.Id));
    }

    [Fact]
    public async Task GetDirectPermissions_ReturnsRevocationsAlways_AndGrantsOnlyWhenApplicable()
    {
        var erp = new Project("ERP", "ERP");
        var crm = new Project("CRM", "CRM");
        var own = new Permission("erp.orders.create", "Create", "Orders", erp.Id);
        var foreign = new Permission("crm.leads.view", "Leads", "Leads", crm.Id);
        var inactive = new Permission("erp.orders.delete", "Delete", "Orders", erp.Id);
        inactive.Deactivate();
        var global = new Permission("users.view", "Users", "Users");

        await SeedAsync(_ => { }, a =>
        {
            a.Projects.AddRange(erp, crm);
            a.Permissions.AddRange(own, foreign, inactive, global);
            a.UserPermissionAssignments.AddRange(
                new UserPermissionAssignment("u1", own.Id, erp.Id),
                new UserPermissionAssignment("u1", foreign.Id, erp.Id),                    // out of scope grant: dropped
                new UserPermissionAssignment("u1", inactive.Id, erp.Id),                   // inactive grant: dropped
                new UserPermissionAssignment("u1", inactive.Id, erp.Id, isRevoked: true),  // revocation: kept
                new UserPermissionAssignment("u1", global.Id, erp.Id, isRevoked: true),
                new UserPermissionAssignment("u1", own.Id, crm.Id),                        // other project
                new UserPermissionAssignment("u2", own.Id, erp.Id));                       // other user
        });

        var direct = await CreateSource().GetDirectPermissionsAsync("u1", erp.Id);

        Assert.Equal(
            new[] { "erp.orders.create:False", "erp.orders.delete:True", "users.view:True" },
            direct.Select(d => $"{d.PermissionCode}:{d.IsRevoked}").Order());
    }
}
