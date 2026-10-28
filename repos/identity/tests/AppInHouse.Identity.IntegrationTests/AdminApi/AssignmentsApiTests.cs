using System.Net;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminApiHost;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminWorld;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

public class AssignmentsApiTests
{
    private static string Assign(Guid projectId) => $"/api/v1/projects/{projectId}/users/assign";

    private static string Roles(Guid projectId, string userId) => $"/api/v1/projects/{projectId}/users/{userId}/roles";

    private static string Permissions(Guid projectId, string userId) => $"/api/v1/projects/{projectId}/users/{userId}/permissions";

    private static Task<bool> IsMemberAsync(AdminApiHost api, Guid projectId, string userId)
        => api.QueryAuthAsync(auth => auth.ProjectUserAssignments.AnyAsync(a => a.ProjectId == projectId && a.UserId == userId));

    private static Task<bool> IsProjectAdminAsync(AdminApiHost api, Guid projectId, string userId)
        => api.QueryAuthAsync(auth => auth.ProjectUserAssignments.AnyAsync(a => a.ProjectId == projectId && a.UserId == userId && a.IsProjectAdmin));

    // ---- assign users to a project ----

    [Fact]
    public async Task ProjectAdmin_AssignsAUserToTheirProject_AndItIsAudited()
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;

        var result = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Assign(projectId), AdminA, new { userId = Outsider }, ip: "198.51.100.4"));

        Assert.Equal(Outsider, result.GetProperty("userId").GetString());
        Assert.False(result.GetProperty("isProjectAdmin").GetBoolean());
        Assert.True(await IsMemberAsync(api, projectId, Outsider));
        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.EntityName == "ProjectUser");
        Assert.Equal("ASSIGN", audit.Action);
        Assert.Equal(AdminA, audit.UserId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal("198.51.100.4", audit.IpAddress);
    }

    [Fact]
    public async Task AssigningTwice_IsIdempotent_AndNotAuditedTwice()
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Assign(projectId), AdminA, new { userId = Outsider }));
        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Assign(projectId), AdminA, new { userId = Outsider }));

        Assert.Single(await api.AuditLogsAsync(), l => l.EntityName == "ProjectUser");
        Assert.Equal(1, await api.QueryAuthAsync(auth => auth.ProjectUserAssignments.CountAsync(a => a.UserId == Outsider)));
    }

    [Fact]
    public async Task ProjectAdmin_CannotGrantProjectAdministratorRights_PrivilegeEscalation()
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;

        // A new member with the flag...
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync(Assign(projectId), AdminA, new { userId = Outsider, isProjectAdmin = true }));
        Assert.False(await IsMemberAsync(api, projectId, Outsider));

        // ...promoting an existing member...
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync(Assign(projectId), AdminA, new { userId = MemberA, isProjectAdmin = true }));
        Assert.False(await IsProjectAdminAsync(api, projectId, MemberA));

        // ...or changing their own flag.
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync(Assign(projectId), AdminA, new { userId = AdminA, isProjectAdmin = false }));
        Assert.True(await IsProjectAdminAsync(api, projectId, AdminA));
        Assert.Empty(await api.AuditLogsAsync());
    }

    [Fact]
    public async Task GlobalAdmin_CanAppointAProjectAdministrator_WhoThenGainsTheirRights()
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;
        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync($"/api/v1/projects/{projectId}", Outsider));

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Assign(projectId), Root, new { userId = Outsider, isProjectAdmin = true }));

        await ExpectAsync(HttpStatusCode.OK, await api.GetAsync($"/api/v1/projects/{projectId}", Outsider));
        Assert.Contains(await api.AuditLogsAsync(), l => l.Action == "ASSIGN" && l.UserId == Root);
    }

    [Fact]
    public async Task GlobalAdmin_CanDemoteAProjectAdministrator()
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Assign(projectId), Root, new { userId = AdminA, isProjectAdmin = false }));

        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync($"/api/v1/projects/{projectId}", AdminA));
        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.Action == "UPDATE");
        Assert.Contains("\"isProjectAdmin\":true", audit.OldValuesJson);
        Assert.Contains("\"isProjectAdmin\":false", audit.NewValuesJson);
    }

    [Theory]
    [InlineData(AdminB)] // administrator of another project (IDOR)
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyAdministratorsOfTheProject_CanAssignUsersToIt(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;

        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync(Assign(projectId), caller, new { userId = Outsider }));

        Assert.False(await IsMemberAsync(api, projectId, Outsider));
    }

    [Fact]
    public async Task Assigning_ToAnUnknownProject_OrOfAnUnknownUser_IsNotFound()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.NotFound, await api.PostAsync(Assign(Guid.NewGuid()), Root, new { userId = Outsider }));
        await ExpectAsync(HttpStatusCode.NotFound, await api.PostAsync(Assign(api.World.ProjectA.Id), Root, new { userId = "nobody" }));
        // Project administrators learn nothing about other projects.
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync(Assign(Guid.NewGuid()), AdminA, new { userId = Outsider }));
    }

    [Fact]
    public async Task Assigning_WithoutAUserId_IsRejected()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(Assign(api.World.ProjectA.Id), Root, new { }));
    }

    // ---- roles in a project ----

    [Fact]
    public async Task AssigningARole_GrantsItsPermissionsImmediately_AndRemovingItRevokesThemImmediately()
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;
        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Assign(projectId), AdminA, new { userId = Outsider }));
        Assert.False(await api.HasPermissionAsync(Outsider, "ProjectA", "orders.create")); // warms the cache with "no"

        var granted = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Roles(projectId, Outsider), AdminA, new { roleIds = new[] { OrderManagerRoleId } }));

        Assert.Equal(OrderManagerRoleId, Assert.Single(granted.GetProperty("roleIds").EnumerateArray()).GetString());
        Assert.True(await api.HasPermissionAsync(Outsider, "ProjectA", "orders.create"));

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Roles(projectId, Outsider), AdminA, new { roleIds = Array.Empty<string>() }));
        Assert.False(await api.HasPermissionAsync(Outsider, "ProjectA", "orders.create"));

        var audits = (await api.AuditLogsAsync()).Where(l => l.Action == "SET_ROLES").ToList();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.Equal(AdminA, a.UserId));
        Assert.Contains(OrderManagerRoleId, audits[0].NewValuesJson);
        Assert.Contains(OrderManagerRoleId, audits[1].OldValuesJson);
    }

    [Fact]
    public async Task SettingRoles_IsAReplacementOfTheWholeSet()
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;
        await api.SeedAsync((identity, _) => identity.Roles.Add(new Infrastructure.Identity.Models.ApplicationRole("Viewer") { Id = "role-viewer", NormalizedName = "VIEWER" }));

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Roles(projectId, MemberA), AdminA, new { roleIds = new[] { "role-viewer" } }));

        var roleIds = await api.QueryAuthAsync(auth => auth.UserRoleProjectAssignments.Where(a => a.UserId == MemberA).Select(a => a.RoleId).ToListAsync());
        Assert.Equal(new[] { "role-viewer" }, roleIds);
        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task Roles_CannotBeSetForAUserWhoIsNotAMember()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(Roles(api.World.ProjectA.Id, Outsider), AdminA, new { roleIds = new[] { OrderManagerRoleId } }));
    }

    [Fact]
    public async Task TheGlobalAdminRole_CannotBeAssignedThroughAProject()
    {
        await using var api = await AdminApiHost.StartAsync();

        var problem = await ExpectAsync(HttpStatusCode.BadRequest,
            await api.PostAsync(Roles(api.World.ProjectA.Id, MemberA), Root, new { roleIds = new[] { GlobalAdminRoleId } }));

        Assert.Contains("GlobalAdmin", problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("unknown-role")]
    [InlineData("")]
    public async Task UnknownOrBlankRoleIds_AreRejected(string roleId)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(Roles(api.World.ProjectA.Id, MemberA), Root, new { roleIds = new[] { roleId } }));
    }

    [Fact]
    public async Task DuplicateRoleIds_AreRejected()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest,
            await api.PostAsync(Roles(api.World.ProjectA.Id, MemberA), Root, new { roleIds = new[] { OrderManagerRoleId, OrderManagerRoleId } }));
    }

    [Theory]
    [InlineData(AdminB)]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyAdministratorsOfTheProject_CanSetRoles(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden,
            await api.PostAsync(Roles(api.World.ProjectA.Id, MemberA), caller, new { roleIds = Array.Empty<string>() }));

        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task AProjectAdmin_CannotReachAnotherProject_ByPuttingItsIdInTheUrl()
    {
        await using var api = await AdminApiHost.StartAsync();
        await api.SeedAsync((_, auth) => auth.ProjectUserAssignments.Add(new Domain.Entities.ProjectUserAssignment(api.World.ProjectB.Id, MemberA)));

        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync(Roles(api.World.ProjectB.Id, MemberA), AdminA, new { roleIds = new[] { OrderManagerRoleId } }));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync(Permissions(api.World.ProjectB.Id, MemberA), AdminA, new { assignments = Array.Empty<object>() }));
    }

    // ---- direct permissions ----

    [Fact]
    public async Task DirectRevocation_BlocksAPermissionEvenThoughTheRoleGrantsIt_Immediately()
    {
        await using var api = await AdminApiHost.StartAsync();
        var projectId = api.World.ProjectA.Id;
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create")); // warms the cache with "yes"

        var result = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Permissions(projectId, MemberA), AdminA,
            new { assignments = new[] { new { permissionId = api.World.OrdersCreateA.Id, isRevoked = true } } }));

        var assignment = Assert.Single(result.GetProperty("assignments").EnumerateArray());
        Assert.Equal("orders.create", assignment.GetProperty("permissionCode").GetString());
        Assert.True(assignment.GetProperty("isRevoked").GetBoolean());
        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));

        // Lifting it (an empty set) restores the role's permission at once.
        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Permissions(projectId, MemberA), AdminA, new { assignments = Array.Empty<object>() }));
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));

        var audits = (await api.AuditLogsAsync()).Where(l => l.Action == "SET_PERMISSIONS").ToList();
        Assert.Equal(2, audits.Count);
        Assert.Contains("\"isRevoked\":true", audits[0].NewValuesJson);
    }

    [Fact]
    public async Task DirectGrant_GivesAGlobalPermission_Immediately()
    {
        await using var api = await AdminApiHost.StartAsync();
        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.view"));

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(Permissions(api.World.ProjectA.Id, MemberA), AdminA,
            new { assignments = new[] { new { permissionId = api.World.OrdersView.Id, isRevoked = false } } }));

        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.view"));
    }

    [Fact]
    public async Task APermissionOfAnotherProject_CannotBeAssignedHere()
    {
        await using var api = await AdminApiHost.StartAsync();

        var problem = await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(Permissions(api.World.ProjectA.Id, MemberA), Root,
            new { assignments = new[] { new { permissionId = api.World.ReportsB.Id, isRevoked = false } } }));

        Assert.Contains("reports.b.view", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task InvalidPermissionAssignments_AreRejected()
    {
        await using var api = await AdminApiHost.StartAsync();
        var url = Permissions(api.World.ProjectA.Id, MemberA);
        var id = api.World.OrdersView.Id;

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, new { assignments = new[] { new { permissionId = Guid.NewGuid(), isRevoked = false } } }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, new { assignments = new[] { new { permissionId = id, isRevoked = false }, new { permissionId = id, isRevoked = true } } }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, new { assignments = new[] { new { isRevoked = false } } }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(Permissions(api.World.ProjectA.Id, Outsider), Root, new { assignments = Array.Empty<object>() })); // not a member
    }

    [Theory]
    [InlineData(AdminB)]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyAdministratorsOfTheProject_CanSetDirectPermissions(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync(Permissions(api.World.ProjectA.Id, MemberA), caller,
            new { assignments = new[] { new { permissionId = api.World.OrdersCreateA.Id, isRevoked = true } } }));

        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }
}
