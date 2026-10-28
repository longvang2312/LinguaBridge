using System.Net;
using AppInHouse.Identity.IntegrationTests.AdminApi;

namespace AppInHouse.Identity.IntegrationTests.AdminWeb;

public class PortalPagesTests : IAsyncLifetime
{
    private AdminWebHost _host = null!;

    public async Task InitializeAsync() => _host = await AdminWebHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    // ---- dashboard ----

    [Fact]
    public async Task Dashboard_RendersTheTotals_ForAGlobalAdministrator()
    {
        var response = await _host.GetAsync("/", AdminWorld.Root);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await BodyAsync(response);
        Assert.Contains("Dashboard", html);
        Assert.Contains("data-stat=\"Projects\">2 / 2<", html);
        Assert.Contains("data-stat=\"Users\">5<", html);
        Assert.Contains("data-stat=\"Roles\">2<", html);
        Assert.Contains("data-stat=\"Permissions\">3<", html);
        Assert.Contains("data-stat=\"Clients\">0<", html);
    }

    [Fact]
    public async Task Dashboard_ForAProjectAdministrator_IsLimitedToTheirProject_AndHidesGlobalFigures()
    {
        var html = await BodyAsync(await _host.GetAsync("/", AdminWorld.AdminA));

        Assert.Contains("data-stat=\"Projects\">1 / 1<", html);
        Assert.Contains("data-stat=\"Permissions\">2<", html); // the global one + the one of project A, not the one of B
        Assert.DoesNotContain("data-stat=\"Users\"", html);
        Assert.DoesNotContain("data-stat=\"Clients\"", html);
        Assert.DoesNotContain("href=\"/Users\"", html); // the menu does not offer what would be refused
    }

    [Fact]
    public async Task Dashboard_ShowsTheRecentAuditEvents()
    {
        await _host.PostFormAsync("/Projects/Create", AdminWorld.Root,
            AdminWebHost.Form(("ProjectCode", "NEWPROJ"), ("Name", "New project")), formPage: "/Projects/Create");

        var html = await BodyAsync(await _host.GetAsync("/", AdminWorld.Root));

        Assert.Contains("CREATE", html);
        Assert.Contains("Project", html);
    }

    [Fact]
    public async Task APortalUserWhoAdministersNothing_IsRefused_NotShownAnything()
    {
        var response = await _host.GetAsync("/", AdminWorld.Outsider);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var html = await BodyAsync(response);
        Assert.Contains("Not allowed", html);
        Assert.DoesNotContain("Project A", html);
    }

    [Fact]
    public async Task AnAnonymousVisitor_IsNotServedAnyPage()
    {
        foreach (var url in new[] { "/", "/Projects", "/Clients", "/Users", "/Roles", "/Roles/Matrix", "/Permissions", "/AuditLogs" })
        {
            var response = await _host.GetAsync(url, userId: null);

            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect, $"{url} answered {response.StatusCode}");
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Projects")]
    [InlineData("/Projects/Create")]
    [InlineData("/Clients")]
    [InlineData("/Clients/Create")]
    [InlineData("/Users")]
    [InlineData("/Users/Create")]
    [InlineData("/Roles")]
    [InlineData("/Roles/Create")]
    [InlineData("/Roles/Matrix")]
    [InlineData("/Permissions")]
    [InlineData("/Permissions/Create")]
    [InlineData("/AuditLogs")]
    public async Task EveryListAndFormPage_RendersForAGlobalAdministrator(string url)
    {
        var response = await _host.GetAsync(url, AdminWorld.Root);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1", await BodyAsync(response));
    }

    [Fact]
    public async Task ProjectAdministrators_CannotOpenThePagesOfTheGlobalAdministrators()
    {
        foreach (var url in new[] { "/Clients", "/Users", "/Roles/Matrix", "/Projects/Create" })
        {
            var response = await _host.GetAsync(url, AdminWorld.AdminA);

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{url} answered {response.StatusCode}");
        }
    }

    // ---- projects ----

    [Fact]
    public async Task ProjectExplorer_ListsEveryProject_ForAGlobalAdministrator_AndSearches()
    {
        var all = await BodyAsync(await _host.GetAsync("/Projects", AdminWorld.Root));
        Assert.Contains("ProjectA", all);
        Assert.Contains("ProjectB", all);

        var found = await BodyAsync(await _host.GetAsync("/Projects?Search=ProjectB", AdminWorld.Root));
        Assert.Contains("ProjectB", found);
        Assert.DoesNotContain("ProjectA", found);
    }

    [Fact]
    public async Task ProjectExplorer_ShowsAProjectAdministratorOnlyTheirOwnProject()
    {
        var html = await BodyAsync(await _host.GetAsync("/Projects", AdminWorld.AdminA));

        Assert.Contains("ProjectA", html);
        Assert.DoesNotContain("ProjectB", html);
    }

    [Fact]
    public async Task AProjectAdministrator_CannotOpenAnotherProject_NorAnUnknownOne()
    {
        var other = await _host.GetAsync($"/Projects/Details/{_host.World.ProjectB.Id}", AdminWorld.AdminA);
        var unknown = await _host.GetAsync($"/Projects/Details/{Guid.NewGuid()}", AdminWorld.AdminA);

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode); // the same answer: existence is not revealed
        Assert.DoesNotContain("Project B", await BodyAsync(other));
    }

    [Fact]
    public async Task ProjectDetails_ListsTheMembersWithTheirRoles()
    {
        var html = await BodyAsync(await _host.GetAsync($"/Projects/Details/{_host.World.ProjectA.Id}", AdminWorld.AdminA));

        Assert.Contains("member-a@test.local", html);
        Assert.Contains("OrderManager", html);
        Assert.Contains("Project admin", html);
    }

    [Fact]
    public async Task CreatingAProject_WorksThroughTheForm_AndIsAudited()
    {
        var response = await _host.PostFormAsync("/Projects/Create", AdminWorld.Root,
            AdminWebHost.Form(("ProjectCode", "ERP"), ("Name", "ERP system"), ("Description", "Orders")), formPage: "/Projects/Create");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var created = await _host.QueryAuthAsync(c => c.Projects.FindAsync(Guid.Parse(response.Headers.Location!.OriginalString.Split('/').Last())).AsTask());
        Assert.Equal("ERP", created!.ProjectCode);

        var log = Assert.Single(await _host.AuditLogsAsync(), l => l.EntityName == "Project");
        Assert.Equal("CREATE", log.Action);
        Assert.Equal(AdminWorld.Root, log.UserId);
    }

    [Fact]
    public async Task AnInvalidProjectForm_IsShownAgainWithTheErrors_AndNothingIsCreated()
    {
        var response = await _host.PostFormAsync("/Projects/Create", AdminWorld.Root,
            AdminWebHost.Form(("ProjectCode", ""), ("Name", "")), formPage: "/Projects/Create");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("field-validation-error", await BodyAsync(response));
        Assert.Empty(await _host.AuditLogsAsync());
    }

    [Fact]
    public async Task ADuplicateProject_GoesBackToTheForm_WithAFriendlyMessage()
    {
        var response = await _host.PostFormAsync("/Projects/Create", AdminWorld.Root,
            AdminWebHost.Form(("ProjectCode", "ProjectA"), ("Name", "Again")), formPage: "/Projects/Create");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Projects/Create", response.Headers.Location!.OriginalString);

        var html = await BodyAsync(await _host.GetAsync("/Projects/Create", AdminWorld.Root));
        Assert.Contains("already exists", html);
    }

    [Fact]
    public async Task LockingAProject_FromThePortal_TakesEffectImmediately()
    {
        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));

        var response = await _host.PostFormAsync(
            $"/Projects/SetStatus/{_host.World.ProjectA.Id}", AdminWorld.Root,
            AdminWebHost.Form(("isActive", "false")), formPage: $"/Projects/Details/{_host.World.ProjectA.Id}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task AProjectAdministrator_CannotLockTheirProject()
    {
        var response = await _host.PostFormAsync(
            $"/Projects/SetStatus/{_host.World.ProjectA.Id}", AdminWorld.AdminA,
            AdminWebHost.Form(("isActive", "false")), formPage: $"/Projects/Details/{_host.World.ProjectA.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));
    }

    // ---- member access ----

    [Fact]
    public async Task GrantingARole_ToAMember_AppliesAtOnce_AndIsAudited()
    {
        Assert.False(await _host.HasPermissionAsync(AdminWorld.Outsider, "ProjectA", "orders.create"));
        var projectId = _host.World.ProjectA.Id;

        var assign = await _host.PostFormAsync($"/Projects/AssignUser/{projectId}", AdminWorld.AdminA,
            AdminWebHost.Form(("UserId", AdminWorld.Outsider)), formPage: $"/Projects/Details/{projectId}");
        Assert.Equal(HttpStatusCode.Redirect, assign.StatusCode);

        var roles = await _host.PostFormAsync($"/Projects/SetRoles/{projectId}?userId={AdminWorld.Outsider}", AdminWorld.AdminA,
            AdminWebHost.Form(("roleIds", AdminWorld.OrderManagerRoleId)), formPage: $"/Projects/Member/{projectId}?userId={AdminWorld.Outsider}");
        Assert.Equal(HttpStatusCode.Redirect, roles.StatusCode);

        Assert.True(await _host.HasPermissionAsync(AdminWorld.Outsider, "ProjectA", "orders.create"));
        Assert.Contains(await _host.AuditLogsAsync(), l => l.Action == "SET_ROLES" && l.UserId == AdminWorld.AdminA);
    }

    [Fact]
    public async Task ARevokedDirectPermission_BeatsTheRole()
    {
        var projectId = _host.World.ProjectA.Id;
        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));

        var response = await _host.PostFormAsync(
            $"/Projects/SetPermissions/{projectId}?userId={AdminWorld.MemberA}", AdminWorld.AdminA,
            AdminWebHost.Form(($"states[{_host.World.OrdersCreateA.Id}]", "revoke")),
            formPage: $"/Projects/Member/{projectId}?userId={AdminWorld.MemberA}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task AProjectAdministrator_CannotManageAnotherProjectsMembers()
    {
        var projectB = _host.World.ProjectB.Id;

        var response = await _host.PostFormAsync($"/Projects/AssignUser/{projectB}", AdminWorld.AdminA,
            AdminWebHost.Form(("UserId", AdminWorld.Outsider)), formPage: $"/Projects/Details/{_host.World.ProjectA.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await _host.QueryAuthAsync(c => Task.FromResult(c.ProjectUserAssignments.Any(a => a.ProjectId == projectB && a.UserId == AdminWorld.Outsider))));
    }

    [Fact]
    public async Task AProjectAdministrator_CannotMakeAnAdministrator_ThroughTheForm()
    {
        var projectId = _host.World.ProjectA.Id;

        var response = await _host.PostFormAsync($"/Projects/AssignUser/{projectId}", AdminWorld.AdminA,
            AdminWebHost.Form(("UserId", AdminWorld.Outsider), ("IsProjectAdmin", "true")), formPage: $"/Projects/Details/{projectId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await _host.QueryAuthAsync(c => Task.FromResult(c.ProjectUserAssignments.Any(a => a.UserId == AdminWorld.Outsider))));
    }

    // ---- audit ----

    [Fact]
    public async Task AuditTrail_ListsEvents_FiltersThem_AndCarriesTheBeforeAfterSnapshots()
    {
        await _host.PostFormAsync($"/Projects/Edit/{_host.World.ProjectA.Id}", AdminWorld.Root,
            AdminWebHost.Form(("Form.Name", "Renamed A"), ("Form.Description", "d")), formPage: $"/Projects/Edit/{_host.World.ProjectA.Id}");

        var html = await BodyAsync(await _host.GetAsync("/AuditLogs?EntityName=Project&Operation=UPDATE", AdminWorld.Root));

        Assert.Contains("UPDATE", html);
        Assert.Contains("data-bs-target=\"#auditModal\"", html);
        Assert.Contains("Renamed A", html);   // new snapshot, HTML-encoded into a data attribute
        Assert.Contains("Project A", html);   // old snapshot

        var none = await BodyAsync(await _host.GetAsync("/AuditLogs?EntityName=Role", AdminWorld.Root));
        Assert.Contains("No event matches", none);
    }

    [Fact]
    public async Task AuditTrail_ShowsAProjectAdministratorOnlyTheirProject()
    {
        await _host.PostFormAsync($"/Projects/Edit/{_host.World.ProjectA.Id}", AdminWorld.Root,
            AdminWebHost.Form(("Form.Name", "Visible A")), formPage: $"/Projects/Edit/{_host.World.ProjectA.Id}");
        await _host.PostFormAsync($"/Projects/Edit/{_host.World.ProjectB.Id}", AdminWorld.Root,
            AdminWebHost.Form(("Form.Name", "Hidden B")), formPage: $"/Projects/Edit/{_host.World.ProjectB.Id}");

        var html = await BodyAsync(await _host.GetAsync("/AuditLogs", AdminWorld.AdminA));

        Assert.Contains("Visible A", html);
        Assert.DoesNotContain("Hidden B", html);
    }

    [Fact]
    public async Task AuditTrail_IgnoresAMalformedFilter_InsteadOfFailing()
    {
        var response = await _host.GetAsync("/AuditLogs?ProjectId=not-a-guid&From=yesterday", AdminWorld.Root);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
