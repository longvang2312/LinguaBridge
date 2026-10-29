using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using LinguaBridge.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LinguaBridge.IntegrationTests;

/// <summary>
/// The default factory runs the real OpenIddict validation scheme; requests there carry no token, so nothing is
/// introspected and the STS does not need to be running.
/// </summary>
public class AuthenticationTests(ApiHostTests.LinguaBridgeApiFactory realAuth, AuthenticationTests.TestAuthFactory testAuth)
    : IClassFixture<ApiHostTests.LinguaBridgeApiFactory>, IClassFixture<AuthenticationTests.TestAuthFactory>
{
    private static readonly Guid UserId = Guid.Parse("3f2c1d4e-5a6b-4c7d-8e9f-0a1b2c3d4e5f");

    [Fact]
    public async Task Health_is_open_without_a_token()
    {
        using var client = realAuth.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Swagger_is_open_and_describes_bearer_security()
    {
        using var client = realAuth.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bearer = document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Contains("Bearer", document.RootElement.GetProperty("security").EnumerateArray().SelectMany(r => r.EnumerateObject()).Select(p => p.Name));
    }

    [Fact]
    public async Task Me_without_a_token_is_unauthorized_by_the_fallback_policy()
    {
        using var client = realAuth.CreateClient();

        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_without_the_test_header_is_unauthorized_on_the_test_scheme_too()
    {
        using var client = testAuth.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Me_with_a_test_user_returns_that_user()
    {
        using var client = testAuth.CreateClient();
        using var request = TestRequest("/api/v1/me", UserId.ToString(), client: "mobile-app");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(UserId, me.GetProperty("userId").GetGuid());
        Assert.Equal("mobile-app", me.GetProperty("clientId").GetString());
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
    }

    [Fact]
    public async Task Me_with_a_non_guid_subject_has_no_user_id()
    {
        // E.g. a client_credentials token: authenticated, but the subject is not a learner.
        using var client = testAuth.CreateClient();
        using var request = TestRequest("/api/v1/me", "internal-service");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, me.GetProperty("userId").ValueKind);
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
    }

    [Fact]
    public async Task Health_stays_open_on_the_test_scheme()
    {
        using var client = testAuth.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public void Current_user_reads_claims_from_the_request_principal()
    {
        using var scope = testAuth.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", UserId.ToString()), new Claim("azp", "web-app"), new Claim("role", "ContentAdmin")],
                authenticationType: "Test")),
        };

        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.True(user.IsAuthenticated);
        Assert.Equal(UserId, user.UserId);
        Assert.Equal("web-app", user.ClientId);
        Assert.True(user.IsInRole("ContentAdmin"));
        Assert.False(user.IsInRole("contentadmin"));
        Assert.False(user.IsInRole("GlobalAdmin"));
    }

    [Fact]
    public void Current_user_is_anonymous_outside_a_request()
    {
        using var scope = testAuth.Services.CreateScope();

        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.False(user.IsAuthenticated);
        Assert.Null(user.UserId);
        Assert.Null(user.ClientId);
        Assert.False(user.IsInRole("ContentAdmin"));
    }

    [Fact]
    public void Production_host_without_an_authority_fails_to_start()
    {
        using var factory = new ProductionFactory(authority: null, secret: "s3cret");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Authentication:Authority", Flatten(ex));
    }

    [Fact]
    public void Production_host_without_a_client_secret_fails_to_start()
    {
        using var factory = new ProductionFactory(authority: "https://sts.example.com", secret: null);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Authentication:ClientSecret", Flatten(ex));
    }

    [Fact]
    public void Production_host_with_complete_auth_configuration_starts()
    {
        using var factory = new ProductionFactory(authority: "https://sts.example.com", secret: "s3cret");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    private static HttpRequestMessage TestRequest(string path, string user, string? roles = null, string? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        if (roles is not null)
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        if (client is not null)
            request.Headers.Add(TestAuthHandler.ClientHeader, client);
        return request;
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
            messages.Add(current.Message);
        return string.Join(" | ", messages);
    }

    /// <summary>Replaces the OpenIddict scheme with <see cref="TestAuthHandler"/> as the default scheme.</summary>
    public sealed class TestAuthFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureTestServices(services =>
                services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
        }
    }

    private sealed class ProductionFactory(string? authority, string? secret) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Authority"] = authority ?? string.Empty,
                    ["Authentication:ClientSecret"] = secret ?? string.Empty,
                }));
        }
    }
}
