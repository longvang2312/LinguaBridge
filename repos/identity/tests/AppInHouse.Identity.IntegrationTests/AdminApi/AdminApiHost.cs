using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppInHouse.Identity.Admin.Api;
using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

/// <summary>
/// The real Admin API (all controllers, use cases, EF Core model, audit, cache and Swagger) hosted in-process on isolated
/// in-memory databases. Only the access-token validation is replaced: <c>Authorization: Bearer {userId}</c> authenticates
/// that user, so the caller's identity is fully controlled by the test while every authorization rule is the production one.
/// </summary>
public sealed class AdminApiHost : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Factory _factory;

    private AdminApiHost(Factory factory)
    {
        _factory = factory;
        Client = factory.CreateClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _factory.Services;

    public AdminWorld World { get; } = new();

    public static async Task<AdminApiHost> StartAsync(bool seedWorld = true)
    {
        var host = new AdminApiHost(new Factory());
        if (seedWorld)
            await host.SeedAsync(host.World.Seed);

        return host;
    }

    // ---- requests ----

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? userId, object? body = null, string? ip = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (userId is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userId);
        if (ip is not null)
            request.Headers.Add("X-Test-Ip", ip);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);

        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string url, string? userId) => SendAsync(HttpMethod.Get, url, userId);

    public Task<HttpResponseMessage> PostAsync(string url, string? userId, object? body = null, string? ip = null)
        => SendAsync(HttpMethod.Post, url, userId, body ?? new { }, ip);

    public Task<HttpResponseMessage> PutAsync(string url, string? userId, object body) => SendAsync(HttpMethod.Put, url, userId, body);

    /// <summary>Asserts the status code (showing the body when it differs) and returns the parsed JSON body, if any.</summary>
    public static async Task<JsonElement> ExpectAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} {expected} but got {(int)response.StatusCode} {response.StatusCode}: {text}");

        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    // ---- data access ----

    public async Task ScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    public async Task<T> ScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task SeedAsync(Action<AppIdentityDbContext, AppAuthDbContext> seed)
        => ScopeAsync(async sp =>
        {
            var identity = sp.GetRequiredService<AppIdentityDbContext>();
            var auth = sp.GetRequiredService<AppAuthDbContext>();
            seed(identity, auth);
            await identity.SaveChangesAsync();
            await auth.SaveChangesAsync();
        });

    public Task<T> QueryAuthAsync<T>(Func<AppAuthDbContext, Task<T>> query)
        => ScopeAsync(sp => query(sp.GetRequiredService<AppAuthDbContext>()));

    public Task<T> QueryIdentityAsync<T>(Func<AppIdentityDbContext, Task<T>> query)
        => ScopeAsync(sp => query(sp.GetRequiredService<AppIdentityDbContext>()));

    public Task<List<AuditLog>> AuditLogsAsync()
        => ScopeAsync(sp => sp.GetRequiredService<AppAuditDbContext>().AuditLogs.AsNoTracking().OrderBy(l => l.Timestamp).ToListAsync());

    public Task<bool> HasPermissionAsync(string userId, string projectCode, string permission)
        => ScopeAsync(sp => sp.GetRequiredService<IPermissionService>().HasPermissionAsync(userId, projectCode, permission));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }

    // ---- hosting ----

    private sealed class Factory : WebApplicationFactory<AdminApiMarker>
    {
        private readonly string _id = Guid.NewGuid().ToString("N");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            // The OpenIddict validation handler still needs an issuer to be configured even though it is not the default
            // scheme in these tests; it is never contacted.
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = "https://sts.invalid/",
                ["Authentication:ClientId"] = "identity_admin_api",
                ["Authentication:ClientSecret"] = "not-used-in-tests",
            }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                services.RemoveAll<DbContextOptions<AppAuthDbContext>>();
                services.RemoveAll<DbContextOptions<AppAuditDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.AddDbContext<AppIdentityDbContext>(o => o.UseInMemoryDatabase("identity-" + _id));
                services.AddDbContext<AppAuthDbContext>(o => o.UseInMemoryDatabase("auth-" + _id));
                services.AddDbContext<AppAuditDbContext>(o => o.UseInMemoryDatabase("audit-" + _id));

                services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                services.AddTransient<IStartupFilter, TestIpStartupFilter>();
            });

            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
    }

    /// <summary>Lets a test choose the client IP address of a request through <c>X-Test-Ip</c>.</summary>
    private sealed class TestIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-Ip", out var ip))
                        context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());

                    return nextMiddleware(context);
                });
                next(app);
            };
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(new[] { new Claim("sub", header["Bearer ".Length..]) }, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

