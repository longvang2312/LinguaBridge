using System.Net;
using System.Text.Json;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

public class SwaggerAndSecurityTests
{
    [Fact]
    public async Task SwaggerDocument_DescribesEveryV1Endpoint()
    {
        await using var api = await AdminApiHost.StartAsync();

        var doc = await AdminApiHost.ExpectAsync(HttpStatusCode.OK, await api.Client.GetAsync("/swagger/v1/swagger.json"));

        var operations = doc.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(m => $"{m.Name.ToUpperInvariant()} {p.Name}"))
            .ToHashSet();

        var expected = new[]
        {
            "GET /api/v1/projects", "GET /api/v1/projects/{id}", "POST /api/v1/projects", "PUT /api/v1/projects/{id}",
            "POST /api/v1/projects/{id}/status",
            "GET /api/v1/clients", "POST /api/v1/clients", "PUT /api/v1/clients/{id}", "POST /api/v1/clients/{id}/rotate-secret",
            "GET /api/v1/users", "POST /api/v1/users", "GET /api/v1/users/{id}", "PUT /api/v1/users/{id}",
            "POST /api/v1/users/{id}/lock", "POST /api/v1/users/{id}/unlock", "POST /api/v1/users/{id}/reset-password",
            "GET /api/v1/permissions", "POST /api/v1/permissions", "PUT /api/v1/permissions/{id}",
            "GET /api/v1/roles", "POST /api/v1/roles", "PUT /api/v1/roles/{id}", "POST /api/v1/roles/{id}/permissions",
            "POST /api/v1/projects/{projectId}/users/assign",
            "POST /api/v1/projects/{projectId}/users/{userId}/roles",
            "POST /api/v1/projects/{projectId}/users/{userId}/permissions",
            "GET /api/v1/audit-logs",
        };

        var missing = expected.Where(e => !operations.Contains(e)).ToList();
        Assert.True(missing.Count == 0, $"Missing from swagger.json: {string.Join("; ", missing)}");
    }

    [Fact]
    public async Task SwaggerDocument_HasMetadataSummariesAndBearerSecurity()
    {
        await using var api = await AdminApiHost.StartAsync();

        var doc = await AdminApiHost.ExpectAsync(HttpStatusCode.OK, await api.Client.GetAsync("/swagger/v1/swagger.json"));

        Assert.Equal("v1", doc.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal("bearer", doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());

        var rotate = doc.GetProperty("paths").GetProperty("/api/v1/clients/{id}/rotate-secret").GetProperty("post");
        Assert.False(string.IsNullOrWhiteSpace(rotate.GetProperty("summary").GetString()));

        // Swashbuckle emits a global security requirement, which applies to every operation.
        Assert.Contains(doc.GetProperty("security").EnumerateArray(), requirement => requirement.TryGetProperty("Bearer", out _));
    }

    [Fact]
    public async Task SwaggerUi_IsServed()
    {
        await using var api = await AdminApiHost.StartAsync();

        var response = await api.Client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("swagger", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GET", "/api/v1/projects")]
    [InlineData("GET", "/api/v1/clients")]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("GET", "/api/v1/permissions")]
    [InlineData("GET", "/api/v1/roles")]
    [InlineData("GET", "/api/v1/audit-logs")]
    [InlineData("POST", "/api/v1/projects")]
    [InlineData("POST", "/api/v1/users")]
    public async Task EveryEndpoint_RequiresAuthentication(string method, string url)
    {
        await using var api = await AdminApiHost.StartAsync();

        var response = await api.SendAsync(new HttpMethod(method), url, userId: null, body: method == "POST" ? new { } : null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownSubject_IsAuthenticatedButHasNoRightsAnywhere()
    {
        await using var api = await AdminApiHost.StartAsync();

        foreach (var url in new[] { "/api/v1/projects", "/api/v1/clients", "/api/v1/users", "/api/v1/permissions", "/api/v1/roles", "/api/v1/audit-logs" })
            Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync(url, "ghost")).StatusCode);
    }

    [Fact]
    public async Task InactiveOrLockedGlobalAdmin_LosesAllRights()
    {
        await using var api = await AdminApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/projects", AdminWorld.Root)).StatusCode);

        await api.SeedAsync((identity, _) => identity.Users.Single(u => u.Id == AdminWorld.Root).LockoutEnd = DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/v1/projects", AdminWorld.Root)).StatusCode);

        await api.SeedAsync((identity, _) =>
        {
            var root = identity.Users.Single(u => u.Id == AdminWorld.Root);
            root.LockoutEnd = null;
            root.IsActive = false;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/v1/projects", AdminWorld.Root)).StatusCode);
    }

    [Fact]
    public async Task ProjectAdmin_OfADeactivatedProject_LosesTheirRights()
    {
        await using var api = await AdminApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/v1/projects/{api.World.ProjectA.Id}", AdminWorld.AdminA)).StatusCode);

        await api.SeedAsync((_, auth) => auth.Projects.Single(p => p.Id == api.World.ProjectA.Id).Deactivate());

        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync($"/api/v1/projects/{api.World.ProjectA.Id}", AdminWorld.AdminA)).StatusCode);
    }

    [Fact]
    public async Task BlockedProjectAdmin_LosesTheirRights()
    {
        await using var api = await AdminApiHost.StartAsync();
        await api.SeedAsync((_, auth) => auth.ProjectUserAssignments.Single(a => a.UserId == AdminWorld.AdminA).Block());

        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync($"/api/v1/projects/{api.World.ProjectA.Id}", AdminWorld.AdminA)).StatusCode);
    }

    [Fact]
    public async Task Errors_AreProblemDetails_WithoutInternals()
    {
        await using var api = await AdminApiHost.StartAsync();

        var response = await api.GetAsync($"/api/v1/projects/{Guid.NewGuid()}", AdminWorld.Root);

        var problem = await AdminApiHost.ExpectAsync(HttpStatusCode.NotFound, response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.DoesNotContain("Exception", problem.ToString());
    }
}
