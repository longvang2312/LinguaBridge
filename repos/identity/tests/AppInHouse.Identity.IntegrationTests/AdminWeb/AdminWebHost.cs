using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AppInHouse.Identity.Admin.Web;
using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using AppInHouse.Identity.IntegrationTests.AdminApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.IntegrationTests.AdminWeb;

/// <summary>
/// The real Admin Web portal (controllers, views, antiforgery, admin use cases, EF Core model, audit and cache) hosted
/// in-process on isolated in-memory databases. Only the sign-in is replaced: <c>Authorization: Bearer {userId}</c>
/// signs that user in, so the identity is controlled by the test while every authorization rule is the production one.
/// </summary>
public sealed partial class AdminWebHost : IAsyncDisposable
{
    private readonly Factory _factory;

    private AdminWebHost(Factory factory)
    {
        _factory = factory;
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _factory.Services;

    public AdminWorld World { get; } = new();

    public static async Task<AdminWebHost> StartAsync()
    {
        var host = new AdminWebHost(new Factory());
        await host.ScopeAsync(async sp =>
        {
            var identity = sp.GetRequiredService<AppIdentityDbContext>();
            var auth = sp.GetRequiredService<AppAuthDbContext>();
            host.World.Seed(identity, auth);
            await identity.SaveChangesAsync();
            await auth.SaveChangesAsync();
        });
        return host;
    }

    // ---- requests ----

    public Task<HttpResponseMessage> GetAsync(string url, string? userId, string? referer = null)
        => SendAsync(HttpMethod.Get, url, userId, null, referer);

    /// <summary>Fetches <paramref name="formPage"/> to obtain a genuine antiforgery token and cookie, then posts the form.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(
        string url, string? userId, IEnumerable<KeyValuePair<string, string>> fields, string? formPage = null, bool withToken = true)
    {
        var body = fields.ToList();
        if (withToken)
        {
            var page = await GetAsync(formPage ?? url, userId);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            body.Add(new("__RequestVerificationToken", TokenIn(await page.Content.ReadAsStringAsync())));
        }

        return await SendAsync(HttpMethod.Post, url, userId, new FormUrlEncodedContent(body), referer: formPage is null ? null : new Uri(Client.BaseAddress!, formPage).ToString());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? userId, HttpContent? content, string? referer = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (userId is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userId);
        if (referer is not null)
            request.Headers.Referrer = new Uri(referer);

        return await Client.SendAsync(request);
    }

    public static string TokenIn(string html)
    {
        var match = TokenPattern().Match(html);
        Assert.True(match.Success, "The page has no antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    public static string Fields(params (string Key, string Value)[] fields) => string.Join("&", fields.Select(f => $"{f.Key}={f.Value}"));

    public static IEnumerable<KeyValuePair<string, string>> Form(params (string Key, string Value)[] fields)
        => fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value));

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

    public Task<T> QueryAuthAsync<T>(Func<AppAuthDbContext, Task<T>> query)
        => ScopeAsync(sp => query(sp.GetRequiredService<AppAuthDbContext>()));

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

    private sealed class Factory : WebApplicationFactory<AdminWebMarker>
    {
        private readonly string _id = Guid.NewGuid().ToString("N");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Required by the OpenID Connect handler to start; the STS is never contacted in these tests.
                ["Authentication:Authority"] = "https://sts.invalid/",
                ["Authentication:ClientId"] = "admin-portal",
                ["Database:MigrateOnStartup"] = "false",
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
            });

            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
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
