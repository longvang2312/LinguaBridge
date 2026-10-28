using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminApiHost;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminWorld;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

public class AuditLogsApiTests
{
    private static JsonElement[] Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToArray();

    private static string[] Field(JsonElement page, string name) => Items(page).Select(i => i.GetProperty(name).GetString() ?? "<null>").ToArray();

    /// <summary>A mix of operations by different actors in different projects.</summary>
    private static async Task<AdminApiHost> StartWithHistoryAsync()
    {
        var api = await AdminApiHost.StartAsync();
        var a = api.World.ProjectA.Id;
        var b = api.World.ProjectB.Id;

        await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/projects/{a}", AdminA, new { name = "A renamed" }));                       // UPDATE Project, A, admin-a
        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/projects/{a}/users/assign", AdminA, new { userId = Outsider }, ip: "203.0.113.9")); // ASSIGN ProjectUser, A
        await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/projects/{b}", AdminB, new { name = "B renamed" }));                       // UPDATE Project, B, admin-b
        await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/permissions", Root, new { permissionCode = "b.extra", displayName = "Extra", module = "B", projectId = b })); // CREATE Permission, B
        await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/roles", Root, new { name = "Auditor" }));                             // CREATE Role, no project
        await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/projects/{b}/status", Root, new { isActive = true }));                    // STATUS_CHANGE Project, B
        return api;
    }

    // ---- search ----

    [Fact]
    public async Task GlobalAdmin_SeesEveryEntry_NewestFirst_WithAllRecordedFields()
    {
        await using var api = await StartWithHistoryAsync();

        var page = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs", Root));

        Assert.Equal(6, page.GetProperty("totalCount").GetInt64());
        Assert.Equal(new[] { "STATUS_CHANGE", "CREATE", "CREATE", "UPDATE", "ASSIGN", "UPDATE" }, Field(page, "action"));
        var assign = Items(page).Single(i => i.GetProperty("action").GetString() == "ASSIGN");
        Assert.Equal(AdminA, assign.GetProperty("userId").GetString());
        Assert.Equal(api.World.ProjectA.Id, assign.GetProperty("projectId").GetGuid());
        Assert.Equal("ProjectUser", assign.GetProperty("entityName").GetString());
        Assert.Equal($"{api.World.ProjectA.Id}:{Outsider}", assign.GetProperty("entityId").GetString());
        Assert.Equal("203.0.113.9", assign.GetProperty("ipAddress").GetString());
        Assert.NotEqual(JsonValueKind.Null, assign.GetProperty("newValuesJson").ValueKind);
        Assert.True(assign.GetProperty("timestamp").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Entries_CanBeFilteredByActorProjectEntityAndAction()
    {
        await using var api = await StartWithHistoryAsync();

        var byActor = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync($"/api/v1/audit-logs?actorId={AdminB}", Root));
        Assert.Equal(new[] { "UPDATE" }, Field(byActor, "action"));

        var byProject = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync($"/api/v1/audit-logs?projectId={api.World.ProjectB.Id}", Root));
        Assert.Equal(3, byProject.GetProperty("totalCount").GetInt64());

        var byEntity = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs?entityName=Project", Root));
        Assert.Equal(3, byEntity.GetProperty("totalCount").GetInt64());

        var byAction = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs?action=CREATE", Root));
        Assert.Equal(new[] { "Role", "Permission" }, Field(byAction, "entityName"));

        var combined = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync($"/api/v1/audit-logs?entityName=Project&action=UPDATE&actorId={AdminA}", Root));
        Assert.Equal(api.World.ProjectA.Id, Assert.Single(Items(combined)).GetProperty("projectId").GetGuid());

        var none = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs?action=DOES_NOT_EXIST", Root));
        Assert.Equal(0, none.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task Entries_CanBeFilteredByTimeRange()
    {
        await using var api = await StartWithHistoryAsync();
        var now = DateTimeOffset.UtcNow;
        string Url(DateTimeOffset? from, DateTimeOffset? to)
            => "/api/v1/audit-logs?" + string.Join("&", new[]
            {
                from is null ? null : $"from={Uri.EscapeDataString(from.Value.ToString("O"))}",
                to is null ? null : $"to={Uri.EscapeDataString(to.Value.ToString("O"))}",
            }.Where(p => p is not null));

        var all = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync(Url(now.AddHours(-1), now.AddHours(1)), Root));
        var future = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync(Url(now.AddHours(1), null), Root));
        var past = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync(Url(null, now.AddHours(-1)), Root));

        Assert.Equal(6, all.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, future.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, past.GetProperty("totalCount").GetInt64());
        await ExpectAsync(HttpStatusCode.BadRequest, await api.GetAsync(Url(now.AddHours(1), now.AddHours(-1)), Root));
    }

    [Fact]
    public async Task Entries_ArePaged()
    {
        await using var api = await StartWithHistoryAsync();

        var first = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs?page=1&pageSize=4", Root));
        var second = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs?page=2&pageSize=4", Root));

        Assert.Equal(4, first.GetProperty("items").GetArrayLength());
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        Assert.Equal(6, second.GetProperty("totalCount").GetInt64());
        Assert.Empty(Field(first, "id").Intersect(Field(second, "id")));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task InvalidPaging_IsRejected(string query)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.GetAsync($"/api/v1/audit-logs?{query}", Root));
    }

    // ---- who may read what ----

    [Fact]
    public async Task ProjectAdmin_SeesOnlyTheEntriesOfTheirOwnProject()
    {
        await using var api = await StartWithHistoryAsync();

        var page = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs", AdminA));

        Assert.Equal(2, page.GetProperty("totalCount").GetInt64());
        Assert.All(Items(page), i => Assert.Equal(api.World.ProjectA.Id, i.GetProperty("projectId").GetGuid()));
        // Platform-level entries (no project, e.g. the new role) are not theirs to see.
        Assert.DoesNotContain("Role", Field(page, "entityName"));
    }

    [Fact]
    public async Task ProjectAdmin_CannotAskForAnotherProjectsEntries_NorBypassTheScopeWithFilters()
    {
        await using var api = await StartWithHistoryAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync($"/api/v1/audit-logs?projectId={api.World.ProjectB.Id}", AdminA));

        // Other filters never widen the visible set.
        var filtered = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync($"/api/v1/audit-logs?actorId={AdminB}", AdminA));
        Assert.Equal(0, filtered.GetProperty("totalCount").GetInt64());
        var byEntity = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs?entityName=Permission", AdminA));
        Assert.Equal(0, byEntity.GetProperty("totalCount").GetInt64());
    }

    [Theory]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task NonAdministrators_CannotReadTheAuditTrail(string caller)
    {
        await using var api = await StartWithHistoryAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync("/api/v1/audit-logs", caller));
    }

    [Fact]
    public async Task ReadingTheAuditTrail_ItselfLeavesNoEntries_AndEntriesCannotBeChangedThroughTheApi()
    {
        await using var api = await StartWithHistoryAsync();
        var before = (await api.AuditLogsAsync()).Count;

        await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/audit-logs", Root));

        Assert.Equal(before, (await api.AuditLogsAsync()).Count);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.SendAsync(method, "/api/v1/audit-logs", Root, new { })).StatusCode);
    }

    // ---- secrets ----

    [Fact]
    public async Task NoSecretOrPassword_EverReachesTheAuditTrail()
    {
        await using var api = await AdminApiHost.StartAsync();
        await api.ScopeAsync(sp => sp.GetRequiredService<IOpenIddictScopeManager>().CreateAsync(new OpenIddictScopeDescriptor { Name = "orders_api" }).AsTask());
        const string password = "Sup3r!Secret#Pass1";
        const string newPassword = "Sup3r!Secret#Pass2";

        var user = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/users", Root, new { email = "audit.user@test.local", fullName = "Audit User", password }));
        await ExpectAsync(HttpStatusCode.NoContent, await api.PostAsync($"/api/v1/users/{user.GetProperty("id").GetString()}/reset-password", Root, new { newPassword }));
        var client = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, new { clientId = "audit-svc", displayName = "Svc", type = "Machine", scopes = new[] { "orders_api" } }));
        var rotated = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/clients/{client.GetProperty("client").GetProperty("id").GetString()}/rotate-secret", Root));

        var all = await api.GetAsync("/api/v1/audit-logs?pageSize=100", Root);
        var json = await all.Content.ReadAsStringAsync();

        Assert.Equal(4, JsonDocument.Parse(json).RootElement.GetProperty("totalCount").GetInt64());
        foreach (var secret in new[] { password, newPassword, client.GetProperty("clientSecret").GetString()!, rotated.GetProperty("clientSecret").GetString()! })
            Assert.DoesNotContain(secret, json);
    }
}
