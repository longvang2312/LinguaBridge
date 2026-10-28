using System.Net;
using System.Text.Json;
using AppInHouse.Identity.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminApiHost;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminWorld;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

public class ProjectsApiTests
{
    private static object NewProject(string code = "Crm", string name = "CRM") => new { projectCode = code, name, description = "Customer relations" };

    // ---- create ----

    [Fact]
    public async Task GlobalAdmin_CreatesAProject_AndItIsAuditedWithTheClientIp()
    {
        await using var api = await AdminApiHost.StartAsync();

        var response = await api.PostAsync("/api/v1/projects", Root, NewProject(), ip: "203.0.113.7");

        var created = await ExpectAsync(HttpStatusCode.Created, response);
        var id = created.GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/v1/projects/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal("Crm", created.GetProperty("projectCode").GetString());
        Assert.True(created.GetProperty("isActive").GetBoolean());

        var fetched = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync($"/api/v1/projects/{id}", Root));
        Assert.Equal("CRM", fetched.GetProperty("name").GetString());

        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.EntityName == "Project");
        Assert.Equal("CREATE", audit.Action);
        Assert.Equal(Root, audit.UserId);
        Assert.Equal(id, audit.ProjectId);
        Assert.Equal(id.ToString(), audit.EntityId);
        Assert.Equal("203.0.113.7", audit.IpAddress);
        Assert.Null(audit.OldValuesJson);
        Assert.Contains("\"projectCode\":\"Crm\"", audit.NewValuesJson);
        Assert.Equal(DateTimeKind.Utc, audit.Timestamp.Kind);
    }

    [Fact]
    public async Task ANewProject_IsUsableImmediately_WithoutAnyCodeChange()
    {
        await using var api = await AdminApiHost.StartAsync();

        var project = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/projects", Root, NewProject()));
        var projectId = project.GetProperty("id").GetGuid();
        var permission = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/permissions", Root,
            new { permissionCode = "crm.leads.view", displayName = "View leads", module = "Leads", projectId }));
        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/projects/{projectId}/users/assign", Root, new { userId = Outsider }));
        Assert.False(await api.HasPermissionAsync(Outsider, "Crm", "crm.leads.view"));

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/projects/{projectId}/users/{Outsider}/permissions", Root,
            new { assignments = new[] { new { permissionId = permission.GetProperty("id").GetGuid(), isRevoked = false } } }));

        Assert.True(await api.HasPermissionAsync(Outsider, "Crm", "crm.leads.view"));
    }

    [Theory]
    [InlineData("Crm")]
    [InlineData("crm")] // codes are unique ignoring case
    public async Task CreatingADuplicateCode_IsAConflict(string code)
    {
        await using var api = await AdminApiHost.StartAsync();
        await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/projects", Root, NewProject("Crm")));

        await ExpectAsync(HttpStatusCode.Conflict, await api.PostAsync("/api/v1/projects", Root, NewProject(code)));
    }

    [Fact]
    public async Task ACodeCanBeReused_OnceTheProjectIsDeleted()
    {
        await using var api = await AdminApiHost.StartAsync();
        await api.SeedAsync((_, auth) => auth.Projects.Single(p => p.Id == api.World.ProjectB.Id).MarkDeleted());

        await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/projects", Root, NewProject("ProjectB")));
    }

    [Theory]
    [InlineData("", "Name")]
    [InlineData("   ", "Name")]
    [InlineData("Crm", "")]
    public async Task CreatingWithInvalidFields_IsRejectedWithDetails(string code, string name)
    {
        await using var api = await AdminApiHost.StartAsync();

        var problem = await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync("/api/v1/projects", Root, new { projectCode = code, name }));

        Assert.True(problem.TryGetProperty("errors", out var errors) || problem.TryGetProperty("detail", out _));
        Assert.Empty(await api.AuditLogsAsync());
    }

    [Fact]
    public async Task OverlongFields_AreRejected()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync("/api/v1/projects", Root, NewProject(new string('a', Project.ProjectCodeMaxLength + 1))));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync("/api/v1/projects", Root, NewProject("ok", new string('a', Project.NameMaxLength + 1))));
    }

    [Theory]
    [InlineData(AdminA)]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyGlobalAdministrators_CanCreateProjects(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync("/api/v1/projects", caller, NewProject()));

        Assert.Empty(await api.AuditLogsAsync());
    }

    // ---- read ----

    [Fact]
    public async Task GlobalAdmin_ListsEveryProject_WithPagingFilterAndSearch()
    {
        await using var api = await AdminApiHost.StartAsync();
        for (var i = 1; i <= 4; i++)
            await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/projects", Root, NewProject($"Extra{i}", $"Extra project {i}")));
        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/projects/{api.World.ProjectB.Id}/status", Root, new { isActive = false }));

        var all = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/projects", Root));
        Assert.Equal(6, all.GetProperty("totalCount").GetInt64());
        Assert.Equal(new[] { "Extra1", "Extra2", "Extra3", "Extra4", "ProjectA", "ProjectB" },
            all.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("projectCode").GetString()!).ToArray());

        var page2 = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/projects?page=2&pageSize=4", Root));
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page2.GetProperty("page").GetInt32());
        Assert.Equal(4, page2.GetProperty("pageSize").GetInt32());
        Assert.Equal(6, page2.GetProperty("totalCount").GetInt64());

        var beyond = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/projects?page=9&pageSize=4", Root));
        Assert.Equal(0, beyond.GetProperty("items").GetArrayLength());

        var inactive = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/projects?isActive=false", Root));
        Assert.Equal("ProjectB", Assert.Single(inactive.GetProperty("items").EnumerateArray()).GetProperty("projectCode").GetString());

        var search = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/projects?search=Extra%20project%203", Root));
        Assert.Equal("Extra3", Assert.Single(search.GetProperty("items").EnumerateArray()).GetProperty("projectCode").GetString());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=abc")]
    public async Task InvalidPaging_IsRejected(string query)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.GetAsync($"/api/v1/projects?{query}", Root));
    }

    [Fact]
    public async Task DeletedProjects_AreNotListedNorReadable()
    {
        await using var api = await AdminApiHost.StartAsync();
        await api.SeedAsync((_, auth) => auth.Projects.Single(p => p.Id == api.World.ProjectB.Id).MarkDeleted());

        var list = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/projects", Root));
        Assert.Equal("ProjectA", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("projectCode").GetString());
        await ExpectAsync(HttpStatusCode.NotFound, await api.GetAsync($"/api/v1/projects/{api.World.ProjectB.Id}", Root));
    }

    [Fact]
    public async Task ProjectAdmin_SeesOnlyTheirOwnProject()
    {
        await using var api = await AdminApiHost.StartAsync();

        var list = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/projects", AdminA));

        Assert.Equal("ProjectA", Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("projectCode").GetString());
        Assert.Equal(1, list.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task ProjectAdmin_CannotReadAnotherProject_NorLearnWhichIdsExist()
    {
        await using var api = await AdminApiHost.StartAsync();

        // Other project and non-existent project look exactly the same to a project administrator.
        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync($"/api/v1/projects/{api.World.ProjectB.Id}", AdminA));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync($"/api/v1/projects/{Guid.NewGuid()}", AdminA));
        // A global administrator may be told it does not exist.
        await ExpectAsync(HttpStatusCode.NotFound, await api.GetAsync($"/api/v1/projects/{Guid.NewGuid()}", Root));
    }

    [Theory]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task NonAdministrators_CannotListOrReadProjects(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync("/api/v1/projects", caller));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync($"/api/v1/projects/{api.World.ProjectA.Id}", caller));
    }

    // ---- update ----

    [Fact]
    public async Task ProjectAdmin_UpdatesTheirOwnProject_AndItIsAuditedWithOldAndNewValues()
    {
        await using var api = await AdminApiHost.StartAsync();
        var url = $"/api/v1/projects/{api.World.ProjectA.Id}";

        var updated = await ExpectAsync(HttpStatusCode.OK, await api.PutAsync(url, AdminA, new { name = "Renamed A", description = "New text" }));

        Assert.Equal("Renamed A", updated.GetProperty("name").GetString());
        Assert.Equal("ProjectA", updated.GetProperty("projectCode").GetString()); // the code is immutable
        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.Action == "UPDATE");
        Assert.Equal(AdminA, audit.UserId);
        Assert.Equal(api.World.ProjectA.Id, audit.ProjectId);
        Assert.Contains("\"name\":\"Project A\"", audit.OldValuesJson);
        Assert.Contains("\"name\":\"Renamed A\"", audit.NewValuesJson);
    }

    [Theory]
    [InlineData(AdminB)] // administrator of another project (IDOR)
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyAdministratorsOfThatProject_CanUpdateIt(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden,
            await api.PutAsync($"/api/v1/projects/{api.World.ProjectA.Id}", caller, new { name = "Hijacked" }));

        var name = await api.QueryAuthAsync(auth => auth.Projects.Where(p => p.Id == api.World.ProjectA.Id).Select(p => p.Name).SingleAsync());
        Assert.Equal("Project A", name);
        Assert.Empty(await api.AuditLogsAsync());
    }

    [Fact]
    public async Task UpdatingWithAStaleConcurrencyToken_IsAConflict()
    {
        await using var api = await AdminApiHost.StartAsync();
        var url = $"/api/v1/projects/{api.World.ProjectA.Id}";
        var first = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync(url, Root));
        var token = first.GetProperty("concurrencyToken").GetString();

        var ok = await ExpectAsync(HttpStatusCode.OK, await api.PutAsync(url, Root, new { name = "One", concurrencyToken = token }));
        await ExpectAsync(HttpStatusCode.Conflict, await api.PutAsync(url, Root, new { name = "Two", concurrencyToken = token }));

        Assert.NotEqual(token, ok.GetProperty("concurrencyToken").GetString());
        Assert.Equal("One", (await ExpectAsync(HttpStatusCode.OK, await api.GetAsync(url, Root))).GetProperty("name").GetString());
    }

    [Fact]
    public async Task UpdatingAMissingProject_IsNotFoundForGlobalAdmins()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.NotFound, await api.PutAsync($"/api/v1/projects/{Guid.NewGuid()}", Root, new { name = "X" }));
    }

    // ---- status ----

    [Fact]
    public async Task LockingAProject_RevokesEveryonesPermissionsAtOnce_AndReactivatingRestoresThem()
    {
        await using var api = await AdminApiHost.StartAsync();
        var url = $"/api/v1/projects/{api.World.ProjectA.Id}/status";
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create")); // warms the cache

        var locked = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(url, Root, new { isActive = false }));

        Assert.False(locked.GetProperty("isActive").GetBoolean());
        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.Action == "STATUS_CHANGE");
        Assert.Contains("\"isActive\":true", audit.OldValuesJson);
        Assert.Contains("\"isActive\":false", audit.NewValuesJson);

        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync(url, Root, new { isActive = true }));
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }

    [Theory]
    [InlineData(AdminA)] // even the administrator of the project itself
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyGlobalAdministrators_CanChangeTheStatusOfAProject(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden,
            await api.PostAsync($"/api/v1/projects/{api.World.ProjectA.Id}/status", caller, new { isActive = false }));

        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task StatusRequest_WithoutTheFlag_IsRejected_NotTreatedAsDeactivate()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync($"/api/v1/projects/{api.World.ProjectA.Id}/status", Root, new { }));

        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }
}
