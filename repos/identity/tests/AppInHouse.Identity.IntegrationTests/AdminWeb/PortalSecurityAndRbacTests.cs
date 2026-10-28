using System.Net;
using AppInHouse.Identity.IntegrationTests.AdminApi;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.IntegrationTests.AdminWeb;

public class PortalSecurityAndRbacTests : IAsyncLifetime
{
    private AdminWebHost _host = null!;

    public async Task InitializeAsync() => _host = await AdminWebHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private string Cell(string roleId, Guid permissionId) => $"{roleId}|{permissionId}";

    // ---- CSRF ----

    [Fact]
    public async Task APostWithoutAnAntiforgeryToken_IsRejected_AndChangesNothing()
    {
        var response = await _host.PostFormAsync("/Projects/Create", AdminWorld.Root,
            AdminWebHost.Form(("ProjectCode", "CSRF"), ("Name", "Forged")), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _host.AuditLogsAsync());
        Assert.False(await _host.QueryAuthAsync(c => c.Projects.AnyAsync(p => p.ProjectCode == "CSRF")));
    }

    [Fact]
    public async Task APostWithAForgedToken_IsRejected()
    {
        // A genuine cookie (from visiting a form) with a token that does not belong to it.
        await _host.GetAsync("/Projects/Create", AdminWorld.Root);

        var response = await _host.PostFormAsync("/Projects/Create", AdminWorld.Root,
            AdminWebHost.Form(("ProjectCode", "CSRF"), ("Name", "Forged"), ("__RequestVerificationToken", "forged-token")), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/Projects/SetStatus/00000000-0000-0000-0000-000000000001")]
    [InlineData("/Clients/RotateSecret/x")]
    [InlineData("/Users/Unlock/x")]
    [InlineData("/Roles/Matrix")]
    [InlineData("/Permissions/Create")]
    [InlineData("/Account/Logout")]
    public async Task EveryStateChangingEndpoint_RequiresTheToken(string url)
    {
        var response = await _host.PostFormAsync(url, AdminWorld.Root, AdminWebHost.Form(("x", "y")), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StateChangingActions_CannotBeTriggeredByAGet()
    {
        var response = await _host.GetAsync($"/Projects/SetStatus/{_host.World.ProjectA.Id}?isActive=false", AdminWorld.Root);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode); // only POST is served, never a change
        Assert.True(await _host.QueryAuthAsync(c => c.Projects.Where(p => p.Id == _host.World.ProjectA.Id).Select(p => p.IsActive).SingleAsync()));
    }

    [Fact]
    public async Task EveryFormOfThePortal_EmbedsAnAntiforgeryToken()
    {
        foreach (var url in new[] { "/Projects/Create", "/Roles/Matrix", "/Users/Create", "/Clients/Create", $"/Projects/Details/{_host.World.ProjectA.Id}" })
        {
            var html = await BodyAsync(await _host.GetAsync(url, AdminWorld.Root));

            Assert.Contains("__RequestVerificationToken", html);
        }
    }

    // ---- the role matrix ----

    [Fact]
    public async Task RoleMatrix_ShowsTheRolesPermissionsAndWhatEachRoleHolds()
    {
        var html = await BodyAsync(await _host.GetAsync("/Roles/Matrix", AdminWorld.Root));

        Assert.Contains("OrderManager", html);
        Assert.DoesNotContain(">GlobalAdmin<", html);
        Assert.Contains("orders.create", html);
        Assert.Contains("reports.b.view", html);

        // OrderManager holds orders.create only.
        Assert.Contains("checked", CellTag(html, AdminWorld.OrderManagerRoleId, _host.World.OrdersCreateA.Id));
        Assert.DoesNotContain("checked", CellTag(html, AdminWorld.OrderManagerRoleId, _host.World.OrdersView.Id));
    }

    /// <summary>The <c>&lt;input&gt;</c> element of one matrix cell.</summary>
    private static string CellTag(string html, string roleId, Guid permissionId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, $"<input[^>]*value=\"{roleId}\\|{permissionId}\"[^>]*>");
        Assert.True(match.Success, "The matrix cell is missing.");
        return match.Value;
    }

    [Fact]
    public async Task SavingTheMatrix_WithAValidToken_ChangesWhatMembersMayDo_AtOnce_AndIsAudited()
    {
        Assert.False(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.view"));

        var response = await _host.PostFormAsync("/Roles/Matrix", AdminWorld.Root,
            AdminWebHost.Form(
                ("roleIds", AdminWorld.OrderManagerRoleId),
                ("cells", Cell(AdminWorld.OrderManagerRoleId, _host.World.OrdersCreateA.Id)),
                ("cells", Cell(AdminWorld.OrderManagerRoleId, _host.World.OrdersView.Id))),
            formPage: "/Roles/Matrix");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.view")); // cache invalidated
        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));

        var log = Assert.Single(await _host.AuditLogsAsync(), l => l.Action == "SET_PERMISSIONS");
        Assert.Equal(AdminWorld.Root, log.UserId);
        Assert.Equal("Role", log.EntityName);
        Assert.Contains("orders.view", log.NewValuesJson);

        var page = await BodyAsync(await _host.GetAsync("/Roles/Matrix", AdminWorld.Root));
        Assert.Contains("Permissions saved for: OrderManager", page);
    }

    [Fact]
    public async Task UncheckingACell_TakesThePermissionAway_AtOnce()
    {
        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));

        var response = await _host.PostFormAsync("/Roles/Matrix", AdminWorld.Root,
            AdminWebHost.Form(("roleIds", AdminWorld.OrderManagerRoleId)), formPage: "/Roles/Matrix");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task SavingAnUnchangedMatrix_WritesNothing()
    {
        await _host.PostFormAsync("/Roles/Matrix", AdminWorld.Root,
            AdminWebHost.Form(("roleIds", AdminWorld.OrderManagerRoleId), ("cells", Cell(AdminWorld.OrderManagerRoleId, _host.World.OrdersCreateA.Id))),
            formPage: "/Roles/Matrix");

        Assert.Empty(await _host.AuditLogsAsync());
    }

    [Fact]
    public async Task ARoleThatIsNotOnThePage_IsLeftUntouched()
    {
        // Only a role listed in roleIds is evaluated: a forged form cannot strip other roles by omission.
        await _host.PostFormAsync("/Roles/Matrix", AdminWorld.Root, AdminWebHost.Form(("roleIds", "unknown-role")), formPage: "/Roles/Matrix");

        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task AProjectAdministrator_CannotSaveTheMatrix()
    {
        var response = await _host.PostFormAsync("/Roles/Matrix", AdminWorld.AdminA,
            AdminWebHost.Form(("roleIds", AdminWorld.OrderManagerRoleId)), formPage: "/Permissions/Create");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));
    }

    // ---- permissions ----

    [Fact]
    public async Task CreatingAndEditingAPermission_WorksThroughTheForms()
    {
        var created = await _host.PostFormAsync("/Permissions/Create", AdminWorld.Root,
            AdminWebHost.Form(("Form.PermissionCode", "users.export"), ("Form.DisplayName", "Export users"), ("Form.Module", "Users"), ("Form.ProjectId", "")),
            formPage: "/Permissions/Create");
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var permission = await _host.QueryAuthAsync(c => c.Permissions.SingleAsync(p => p.PermissionCode == "users.export"));
        Assert.True(permission.IsGlobal);

        var edited = await _host.PostFormAsync($"/Permissions/Edit/{permission.Id}", AdminWorld.Root,
            AdminWebHost.Form(("DisplayName", "Export all users"), ("Module", "Directory"), ("IsActive", "true")),
            formPage: $"/Permissions/Edit/{permission.Id}");
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);

        var after = await _host.QueryAuthAsync(c => c.Permissions.SingleAsync(p => p.Id == permission.Id));
        Assert.Equal("Export all users", after.DisplayName);
        Assert.Equal("Directory", after.Module);
        Assert.Equal("users.export", after.PermissionCode);
    }

    [Fact]
    public async Task AProjectAdministrator_CanDefineAPermissionOnlyInTheirOwnProject()
    {
        var own = await _host.PostFormAsync("/Permissions/Create", AdminWorld.AdminA,
            AdminWebHost.Form(("Form.PermissionCode", "orders.cancel"), ("Form.DisplayName", "Cancel"), ("Form.Module", "Orders"), ("Form.ProjectId", _host.World.ProjectA.Id.ToString())),
            formPage: "/Permissions/Create");
        var global = await _host.PostFormAsync("/Permissions/Create", AdminWorld.AdminA,
            AdminWebHost.Form(("Form.PermissionCode", "everything.all"), ("Form.DisplayName", "All"), ("Form.Module", "Orders"), ("Form.ProjectId", "")),
            formPage: "/Permissions/Create");
        var foreign = await _host.PostFormAsync("/Permissions/Create", AdminWorld.AdminA,
            AdminWebHost.Form(("Form.PermissionCode", "reports.hack"), ("Form.DisplayName", "x"), ("Form.Module", "Reports"), ("Form.ProjectId", _host.World.ProjectB.Id.ToString())),
            formPage: "/Permissions/Create");

        Assert.Equal(HttpStatusCode.Redirect, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, global.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task DeactivatingAPermission_FromTheForm_RemovesItFromItsHolders_AtOnce()
    {
        var response = await _host.PostFormAsync($"/Permissions/Edit/{_host.World.OrdersCreateA.Id}", AdminWorld.Root,
            AdminWebHost.Form(("DisplayName", "Create orders"), ("Module", "Orders"), ("IsActive", "false")),
            formPage: $"/Permissions/Edit/{_host.World.OrdersCreateA.Id}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));
    }

    // ---- roles ----

    [Fact]
    public async Task CreatingARole_AppearsInTheListAndTheMatrix()
    {
        var response = await _host.PostFormAsync("/Roles/Create", AdminWorld.Root,
            AdminWebHost.Form(("Name", "Auditor"), ("Description", "Read only")), formPage: "/Roles/Create");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Auditor", await BodyAsync(await _host.GetAsync("/Roles", AdminWorld.Root)));
        Assert.Contains("Auditor", await BodyAsync(await _host.GetAsync("/Roles/Matrix", AdminWorld.Root)));
    }

    [Fact]
    public async Task AProjectAdministrator_SeesTheRoles_ButNotWhatTheyCarry()
    {
        var html = await BodyAsync(await _host.GetAsync("/Roles", AdminWorld.AdminA));

        Assert.Contains("OrderManager", html);
        Assert.DoesNotContain("orders.create", html);
    }

    // ---- clients ----

    [Fact]
    public async Task ACreatedClientSecret_IsShownOnce_NeverCached_AndNeverAudited()
    {
        var response = await _host.PostFormAsync("/Clients/Create", AdminWorld.Root,
            AdminWebHost.Form(("Form.ClientId", "erp-api"), ("Form.DisplayName", "ERP API"), ("Form.Type", "Machine")),
            formPage: "/Clients/Create");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var html = await BodyAsync(response);
        var secret = System.Text.RegularExpressions.Regex.Match(html, "data-secret>([^<]+)<").Groups[1].Value;
        Assert.True(secret.Length >= 32, "The secret must be displayed.");

        // Not retrievable afterwards, not even by an administrator.
        var list = await BodyAsync(await _host.GetAsync("/Clients", AdminWorld.Root));
        Assert.Contains("erp-api", list);
        Assert.DoesNotContain(secret, list);

        foreach (var log in await _host.AuditLogsAsync())
        {
            Assert.DoesNotContain(secret, log.NewValuesJson ?? string.Empty);
            Assert.DoesNotContain(secret, log.OldValuesJson ?? string.Empty);
        }
    }

    [Fact]
    public async Task ClientForm_RejectsAnInsecureRedirectUri_WithAMessage()
    {
        var response = await _host.PostFormAsync("/Clients/Create", AdminWorld.Root,
            AdminWebHost.Form(("Form.ClientId", "bad-web"), ("Form.DisplayName", "Bad"), ("Form.Type", "Web"), ("Form.RedirectUris", "http://evil.example/cb")),
            formPage: "/Clients/Create");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("must use https", await BodyAsync(await _host.GetAsync("/Clients/Create", AdminWorld.Root)));
    }

    // ---- users ----

    [Fact]
    public async Task CreatingAUser_NeverAuditsOrEchoesThePassword()
    {
        const string password = "Sup3r!Secret#Pass";

        var response = await _host.PostFormAsync("/Users/Create", AdminWorld.Root,
            AdminWebHost.Form(("Email", "new.user@test.local"), ("FullName", "New User"), ("Password", password)), formPage: "/Users/Create");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("new.user@test.local", await BodyAsync(await _host.GetAsync("/Users?Search=new.user", AdminWorld.Root)));
        foreach (var log in await _host.AuditLogsAsync())
            Assert.DoesNotContain(password, (log.NewValuesJson ?? string.Empty) + log.OldValuesJson);
    }

    [Fact]
    public async Task APasswordThatFailsThePolicy_IsNotEchoedBackInTheForm()
    {
        var response = await _host.PostFormAsync("/Users/Create", AdminWorld.Root,
            AdminWebHost.Form(("Email", "weak@test.local"), ("FullName", "Weak"), ("Password", "weak")), formPage: "/Users/Create");

        var html = await BodyAsync(response.StatusCode == HttpStatusCode.Redirect ? await _host.GetAsync("/Users/Create", AdminWorld.Root) : response);
        Assert.DoesNotContain("value=\"weak\"", html);
    }

    [Fact]
    public async Task LockingAUser_FromThePortal_LocksThemOut_AndUnlockingRestoresThem()
    {
        var lockResponse = await _host.PostFormAsync($"/Users/Lock/{AdminWorld.MemberA}", AdminWorld.Root,
            AdminWebHost.Form(("lockedUntil", "")), formPage: $"/Users/Details/{AdminWorld.MemberA}");
        Assert.Equal(HttpStatusCode.Redirect, lockResponse.StatusCode);
        Assert.False(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));

        await _host.PostFormAsync($"/Users/Unlock/{AdminWorld.MemberA}", AdminWorld.Root, AdminWebHost.Form(), formPage: $"/Users/Details/{AdminWorld.MemberA}");
        Assert.True(await _host.HasPermissionAsync(AdminWorld.MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task AdministratorsCannotLockOrDeactivateThemselves()
    {
        var locked = await _host.PostFormAsync($"/Users/Lock/{AdminWorld.Root}", AdminWorld.Root,
            AdminWebHost.Form(("lockedUntil", "")), formPage: $"/Users/Details/{AdminWorld.Root}");
        var deactivated = await _host.PostFormAsync($"/Users/Update/{AdminWorld.Root}", AdminWorld.Root,
            AdminWebHost.Form(("FullName", "Root"), ("IsActive", "false")), formPage: $"/Users/Details/{AdminWorld.Root}");

        // Refused with a message, not applied.
        Assert.Equal(HttpStatusCode.Redirect, locked.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, deactivated.StatusCode);
        Assert.Contains("own account", await BodyAsync(await _host.GetAsync($"/Users/Details/{AdminWorld.Root}", AdminWorld.Root)));
        Assert.Equal(HttpStatusCode.OK, (await _host.GetAsync("/", AdminWorld.Root)).StatusCode);
    }

    [Fact]
    public async Task AssigningAUserToAProject_FromTheUserPage_Works()
    {
        var response = await _host.PostFormAsync($"/Users/AssignProject/{AdminWorld.Outsider}", AdminWorld.Root,
            AdminWebHost.Form(("projectId", _host.World.ProjectB.Id.ToString()), ("isProjectAdmin", "false")),
            formPage: $"/Users/Details/{AdminWorld.Outsider}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var page = await BodyAsync(await _host.GetAsync($"/Users/Details/{AdminWorld.Outsider}", AdminWorld.Root));
        Assert.Contains("ProjectB", page);
    }

    // ---- error pages ----

    [Fact]
    public async Task AnUnknownRecord_GivesANotFoundPage_NotAnException()
    {
        var response = await _host.GetAsync($"/Clients/Edit/{Guid.NewGuid()}", AdminWorld.Root);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Not found", await BodyAsync(response));
    }

    [Fact]
    public async Task ARefusalNeverRedirectsToAForeignReferer()
    {
        // An attacker-controlled Referer must not turn a refused form into an open redirect.
        var page = await _host.GetAsync("/Projects/Create", AdminWorld.Root);
        var token = AdminWebHost.TokenIn(await BodyAsync(page));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Projects/Create")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("ProjectCode", "ProjectA"),
                new KeyValuePair<string, string>("Name", "Dup"),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminWorld.Root);
        request.Headers.Referrer = new Uri("https://evil.example/phish");

        var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }
}
