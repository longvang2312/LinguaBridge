using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.AspNetCore.Identity;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

/// <summary>
/// The cast of the scenarios: a global administrator, an administrator per project, a plain member, an outsider,
/// two projects, one role carrying one permission.
/// </summary>
public sealed class AdminWorld
{
    public const string Root = "root";           // global administrator
    public const string AdminA = "admin-a";      // administrator of project A only
    public const string AdminB = "admin-b";      // administrator of project B only
    public const string MemberA = "member-a";    // plain member of project A, holds OrderManager there
    public const string Outsider = "outsider";   // exists, administers nothing, belongs to nothing

    public const string GlobalAdminRoleId = "role-global-admin";
    public const string OrderManagerRoleId = "role-order-manager";

    public Project ProjectA { get; } = new("ProjectA", "Project A");
    public Project ProjectB { get; } = new("ProjectB", "Project B");

    /// <summary>Owned by project A, carried by OrderManager.</summary>
    public Permission OrdersCreateA { get; }

    /// <summary>Global.</summary>
    public Permission OrdersView { get; } = new("orders.view", "View orders", "Orders");

    /// <summary>Owned by project B.</summary>
    public Permission ReportsB { get; }

    public AdminWorld()
    {
        OrdersCreateA = new Permission("orders.create", "Create orders", "Orders", ProjectA.Id);
        ReportsB = new Permission("reports.b.view", "View reports (B)", "Reports", ProjectB.Id);
    }

    public static ApplicationUser NewUser(string id, string? email = null) => new()
    {
        Id = id,
        UserName = email ?? $"{id}@test.local",
        NormalizedUserName = (email ?? $"{id}@test.local").ToUpperInvariant(),
        Email = email ?? $"{id}@test.local",
        NormalizedEmail = (email ?? $"{id}@test.local").ToUpperInvariant(),
        EmailConfirmed = true,
        FullName = $"User {id}",
        IsActive = true,
        LockoutEnabled = true,
        SecurityStamp = Guid.NewGuid().ToString(),
    };

    public void Seed(AppIdentityDbContext identity, AppAuthDbContext auth)
    {
        identity.Roles.AddRange(
            new ApplicationRole("GlobalAdmin") { Id = GlobalAdminRoleId, NormalizedName = "GLOBALADMIN" },
            new ApplicationRole("OrderManager") { Id = OrderManagerRoleId, NormalizedName = "ORDERMANAGER" });
        identity.Users.AddRange(NewUser(Root), NewUser(AdminA), NewUser(AdminB), NewUser(MemberA), NewUser(Outsider));
        identity.UserRoles.Add(new IdentityUserRole<string> { UserId = Root, RoleId = GlobalAdminRoleId });

        auth.Projects.AddRange(ProjectA, ProjectB);
        auth.Permissions.AddRange(OrdersCreateA, OrdersView, ReportsB);
        auth.RolePermissions.Add(new RolePermission(OrderManagerRoleId, OrdersCreateA.Id));

        auth.ProjectUserAssignments.AddRange(
            new ProjectUserAssignment(ProjectA.Id, AdminA, isProjectAdmin: true),
            new ProjectUserAssignment(ProjectB.Id, AdminB, isProjectAdmin: true),
            new ProjectUserAssignment(ProjectA.Id, MemberA));
        auth.UserRoleProjectAssignments.Add(new UserRoleProjectAssignment(MemberA, OrderManagerRoleId, ProjectA.Id));
    }
}
