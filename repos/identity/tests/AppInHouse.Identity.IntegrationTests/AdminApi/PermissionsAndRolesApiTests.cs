using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminApiHost;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminWorld;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

public class PermissionsAndRolesApiTests
{
    private static object NewPermission(string code = "users.view", Guid? projectId = null)
        => new { permissionCode = code, displayName = "Display " + code, module = "Users", projectId };

    private static string[] Codes(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("permissionCode").GetString()!).ToArray();

    // ---- permissions: create ----

    [Fact]
    public async Task GlobalAdmin_CreatesGlobalAndProjectPermissions_AndTheyAreAudited()
    {
        await using var api = await AdminApiHost.StartAsync();

        var global = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/permissions", Root, NewPermission("users.view")));
        var scoped = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/permissions", Root, NewPermission("b.use", api.World.ProjectB.Id)));

        Assert.True(global.GetProperty("isGlobal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, global.GetProperty("projectId").ValueKind);
        Assert.False(scoped.GetProperty("isGlobal").GetBoolean());
        Assert.Equal(api.World.ProjectB.Id, scoped.GetProperty("projectId").GetGuid());
        var audits = (await api.AuditLogsAsync()).Where(l => l.EntityName == "Permission").ToList();
        Assert.Equal(2, audits.Count);
        Assert.Null(audits[0].ProjectId);
        Assert.Equal(api.World.ProjectB.Id, audits[1].ProjectId);
    }

    [Fact]
    public async Task ProjectAdmin_CreatesPermissionsOfTheirOwnProject()
    {
        await using var api = await AdminApiHost.StartAsync();

        var created = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/permissions", AdminA, NewPermission("a.export", api.World.ProjectA.Id)));

        Assert.Equal(api.World.ProjectA.Id, created.GetProperty("projectId").GetGuid());
    }

    [Fact]
    public async Task ProjectAdmin_CannotCreateGlobalPermissions_NorPermissionsOfAnotherProject_PrivilegeEscalation()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync("/api/v1/permissions", AdminA, NewPermission("global.sneaky")));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync("/api/v1/permissions", AdminA, NewPermission("b.sneaky", api.World.ProjectB.Id)));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync("/api/v1/permissions", MemberA, NewPermission("a.sneaky", api.World.ProjectA.Id)));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync("/api/v1/permissions", Outsider, NewPermission("any.sneaky")));

        Assert.Equal(3, await api.QueryAuthAsync(auth => auth.Permissions.CountAsync()));
        Assert.Empty(await api.AuditLogsAsync());
    }

    [Theory]
    [InlineData("orders.create")]
    [InlineData("ORDERS.CREATE")] // unique ignoring case
    public async Task DuplicatePermissionCodes_AreAConflict(string code)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Conflict, await api.PostAsync("/api/v1/permissions", Root, NewPermission(code)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("-leading")]
    public async Task InvalidPermissionCodes_AreRejected(string code)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync("/api/v1/permissions", Root, NewPermission(code)));
    }

    [Fact]
    public async Task CreatingAPermissionForAnUnknownProject_IsNotFound()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.NotFound, await api.PostAsync("/api/v1/permissions", Root, NewPermission("x.y", Guid.NewGuid())));
    }

    // ---- permissions: list ----

    [Fact]
    public async Task GlobalAdmin_ListsAllPermissions_WithFilters()
    {
        await using var api = await AdminApiHost.StartAsync();

        Assert.Equal(new[] { "orders.create", "orders.view", "reports.b.view" }, Codes(await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/permissions", Root))));
        Assert.Equal(new[] { "reports.b.view" }, Codes(await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/permissions?module=Reports", Root))));
        Assert.Equal(new[] { "orders.view" }, Codes(await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/permissions?search=view&module=Orders", Root))));
        // projectId = permissions that can apply to the project: its own and the global ones
        Assert.Equal(new[] { "orders.create", "orders.view" },
            Codes(await ExpectAsync(HttpStatusCode.OK, await api.GetAsync($"/api/v1/permissions?projectId={api.World.ProjectA.Id}", Root))));
    }

    [Fact]
    public async Task ProjectAdmin_SeesGlobalAndOwnProjectPermissionsOnly()
    {
        await using var api = await AdminApiHost.StartAsync();

        var visible = Codes(await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/permissions", AdminA)));

        Assert.Equal(new[] { "orders.create", "orders.view" }, visible);
        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync($"/api/v1/permissions?projectId={api.World.ProjectB.Id}", AdminA));
    }

    [Theory]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task NonAdministrators_CannotListPermissions(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync("/api/v1/permissions", caller));
    }

    // ---- permissions: update ----

    [Fact]
    public async Task ProjectAdmin_UpdatesTheirProjectsPermission_AndItIsAudited()
    {
        await using var api = await AdminApiHost.StartAsync();

        var updated = await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/permissions/{api.World.OrdersCreateA.Id}", AdminA,
            new { displayName = "Create sales orders", module = "Sales", isActive = true }));

        Assert.Equal("Create sales orders", updated.GetProperty("displayName").GetString());
        Assert.Equal("Sales", updated.GetProperty("module").GetString());
        Assert.Equal("orders.create", updated.GetProperty("permissionCode").GetString()); // immutable
        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.EntityName == "Permission");
        Assert.Contains("\"displayName\":\"Create orders\"", audit.OldValuesJson);
        Assert.Contains("\"displayName\":\"Create sales orders\"", audit.NewValuesJson);
    }

    [Fact]
    public async Task ProjectAdmin_CannotUpdateGlobalPermissions_OrThoseOfAnotherProject()
    {
        await using var api = await AdminApiHost.StartAsync();
        var body = new { displayName = "Hijacked", module = "X", isActive = true };

        await ExpectAsync(HttpStatusCode.Forbidden, await api.PutAsync($"/api/v1/permissions/{api.World.OrdersView.Id}", AdminA, body));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PutAsync($"/api/v1/permissions/{api.World.ReportsB.Id}", AdminA, body));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PutAsync($"/api/v1/permissions/{api.World.OrdersCreateA.Id}", MemberA, body));

        Assert.Equal("View orders", await api.QueryAuthAsync(auth => auth.Permissions.Where(p => p.Id == api.World.OrdersView.Id).Select(p => p.DisplayName).SingleAsync()));
    }

    [Fact]
    public async Task UnknownPermission_IsNotFoundForGlobalAdmins_ButIndistinguishableForOthers()
    {
        await using var api = await AdminApiHost.StartAsync();
        var body = new { displayName = "X", module = "X", isActive = true };

        await ExpectAsync(HttpStatusCode.NotFound, await api.PutAsync($"/api/v1/permissions/{Guid.NewGuid()}", Root, body));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PutAsync($"/api/v1/permissions/{Guid.NewGuid()}", AdminA, body));
    }

    [Fact]
    public async Task DeactivatingAProjectPermission_RevokesItAtOnce()
    {
        await using var api = await AdminApiHost.StartAsync();
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));

        await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/permissions/{api.World.OrdersCreateA.Id}", AdminA,
            new { displayName = "Create orders", module = "Orders", isActive = false }));

        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task DeactivatingAGlobalPermission_RevokesItEverywhereAtOnce()
    {
        await using var api = await AdminApiHost.StartAsync();
        await api.SeedAsync((_, auth) =>
        {
            auth.UserPermissionAssignments.Add(new Domain.Entities.UserPermissionAssignment(MemberA, api.World.OrdersView.Id, api.World.ProjectA.Id));
        });
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.view"));

        await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/permissions/{api.World.OrdersView.Id}", Root,
            new { displayName = "View orders", module = "Orders", isActive = false }));

        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.view"));
    }

    [Fact]
    public async Task UpdatingAPermission_WithoutTheActiveFlag_IsRejected()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PutAsync($"/api/v1/permissions/{api.World.OrdersView.Id}", Root, new { displayName = "X", module = "Y" }));

        Assert.True(await api.QueryAuthAsync(auth => auth.Permissions.Where(p => p.Id == api.World.OrdersView.Id).Select(p => p.IsActive).SingleAsync()));
    }

    // ---- roles ----

    [Fact]
    public async Task GlobalAdmin_CreatesAndUpdatesRoles_WithAudit()
    {
        await using var api = await AdminApiHost.StartAsync();

        var created = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/roles", Root, new { name = "Auditor", description = "Reads audit data" }));
        var id = created.GetProperty("id").GetString();
        var updated = await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/roles/{id}", Root, new { name = "Senior Auditor", description = "More" }));

        Assert.Equal("Senior Auditor", updated.GetProperty("name").GetString());
        var audits = (await api.AuditLogsAsync()).Where(l => l.EntityName == "Role").ToList();
        Assert.Equal(new[] { "CREATE", "UPDATE" }, audits.Select(a => a.Action));
        Assert.Contains("\"name\":\"Auditor\"", audits[1].OldValuesJson);
        Assert.Contains("\"name\":\"Senior Auditor\"", audits[1].NewValuesJson);
    }

    [Fact]
    public async Task DuplicateRoleNames_AreAConflict()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Conflict, await api.PostAsync("/api/v1/roles", Root, new { name = "OrderManager" }));
        await ExpectAsync(HttpStatusCode.Conflict, await api.PostAsync("/api/v1/roles", Root, new { name = "ordermanager" }));
    }

    [Fact]
    public async Task TheGlobalAdminRole_IsProtected()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PutAsync($"/api/v1/roles/{GlobalAdminRoleId}", Root, new { name = "Renamed" }));
        await ExpectAsync(HttpStatusCode.Conflict, await api.PutAsync($"/api/v1/roles/{OrderManagerRoleId}", Root, new { name = "GlobalAdmin" }));
        Assert.Equal("GlobalAdmin", await api.QueryIdentityAsync(identity => identity.Roles.Where(r => r.Id == GlobalAdminRoleId).Select(r => r.Name!).SingleAsync()));
    }

    [Theory]
    [InlineData(AdminA)]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyGlobalAdministrators_CanCreateOrChangeRoles(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync("/api/v1/roles", caller, new { name = "Sneaky" }));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PutAsync($"/api/v1/roles/{OrderManagerRoleId}", caller, new { name = "Sneaky" }));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync($"/api/v1/roles/{OrderManagerRoleId}/permissions", caller, new { permissionIds = Array.Empty<Guid>() }));

        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
        Assert.Empty(await api.AuditLogsAsync());
    }

    [Fact]
    public async Task RoleList_ShowsPermissionsOnlyToGlobalAdministrators()
    {
        await using var api = await AdminApiHost.StartAsync();

        var asRoot = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/roles?search=Order", Root));
        var manager = Assert.Single(asRoot.GetProperty("items").EnumerateArray());
        Assert.Equal("OrderManager", manager.GetProperty("name").GetString());
        Assert.Equal("orders.create", Assert.Single(manager.GetProperty("permissions").EnumerateArray()).GetProperty("permissionCode").GetString());

        // A project administrator needs the role list to assign roles, but does not learn what the roles carry.
        var asAdmin = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/roles", AdminA));
        Assert.Equal(2, asAdmin.GetProperty("totalCount").GetInt64());
        Assert.All(asAdmin.GetProperty("items").EnumerateArray(), role => Assert.Equal(JsonValueKind.Null, role.GetProperty("permissions").ValueKind));

        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync("/api/v1/roles", Outsider));
    }

    // ---- role permissions ----

    [Fact]
    public async Task ChangingRolePermissions_InvalidatesEveryonesCachedPermissions_Immediately()
    {
        await using var api = await AdminApiHost.StartAsync();
        // A second holder of the same role, in the same project.
        await api.SeedAsync((_, auth) =>
        {
            auth.ProjectUserAssignments.Add(new Domain.Entities.ProjectUserAssignment(api.World.ProjectA.Id, Outsider));
            auth.UserRoleProjectAssignments.Add(new Domain.Entities.UserRoleProjectAssignment(Outsider, OrderManagerRoleId, api.World.ProjectA.Id));
        });
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
        Assert.True(await api.HasPermissionAsync(Outsider, "ProjectA", "orders.create"));

        var result = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/roles/{OrderManagerRoleId}/permissions", Root,
            new { permissionIds = new[] { api.World.OrdersView.Id } }));

        Assert.Equal("orders.view", Assert.Single(result.GetProperty("permissions").EnumerateArray()).GetProperty("permissionCode").GetString());
        foreach (var user in new[] { MemberA, Outsider })
        {
            Assert.False(await api.HasPermissionAsync(user, "ProjectA", "orders.create"));
            Assert.True(await api.HasPermissionAsync(user, "ProjectA", "orders.view"));
        }

        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.Action == "SET_PERMISSIONS");
        Assert.Equal(Root, audit.UserId);
        Assert.Contains("orders.create", audit.OldValuesJson);
        Assert.Contains("orders.view", audit.NewValuesJson);
    }

    [Fact]
    public async Task EmptyingARole_RemovesAllItsPermissions()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/roles/{OrderManagerRoleId}/permissions", Root, new { permissionIds = Array.Empty<Guid>() }));

        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task SettingRolePermissions_IsIdempotent()
    {
        await using var api = await AdminApiHost.StartAsync();
        var body = new { permissionIds = new[] { api.World.OrdersCreateA.Id, api.World.OrdersView.Id } };

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/roles/{OrderManagerRoleId}/permissions", Root, body));
        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/roles/{OrderManagerRoleId}/permissions", Root, body));

        Assert.Equal(2, await api.QueryAuthAsync(auth => auth.RolePermissions.CountAsync(rp => rp.RoleId == OrderManagerRoleId)));
    }

    [Fact]
    public async Task UnknownPermissionsOrRoles_AreRejected()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync($"/api/v1/roles/{OrderManagerRoleId}/permissions", Root, new { permissionIds = new[] { Guid.NewGuid() } }));
        await ExpectAsync(HttpStatusCode.NotFound, await api.PostAsync("/api/v1/roles/nope/permissions", Root, new { permissionIds = Array.Empty<Guid>() }));
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create")); // nothing was changed
    }
}
