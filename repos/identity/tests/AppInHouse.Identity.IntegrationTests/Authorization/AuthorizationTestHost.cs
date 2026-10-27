using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AppInHouse.Identity.Application;
using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.ClientSdk;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Caching;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.IntegrationTests.Authorization;

/// <summary>
/// An in-process API that uses the whole authorization stack exactly as a real host would: EF Core persistence,
/// the permission engine, the in-memory cache and the ClientSdk (policy provider, middleware, handler).
/// Only authentication is faked: <c>Authorization: Bearer {userId}</c> authenticates that user, so reusing one
/// "token" across calls models an access token that stays valid while the permissions behind it change.
/// </summary>
public sealed class AuthorizationTestHost : IAsyncDisposable
{
    public const string Scheme = "Test";

    private readonly WebApplication _app;

    private AuthorizationTestHost(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<AuthorizationTestHost> StartAsync(Action<IServiceCollection>? overrideServices = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddDbContext<AppIdentityDbContext>(o => o.UseInMemoryDatabase("identity-" + id));
        builder.Services.AddDbContext<AppAuthDbContext>(o => o.UseInMemoryDatabase("auth-" + id));

        builder.Services.AddPermissionPersistence();
        builder.Services.AddPermissionCaching();
        builder.Services.AddAuthorizationEngine();
        builder.Services.AddAppInHouseAuthorization();

        builder.Services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(Scheme, _ => { });
        builder.Services.AddControllers().AddApplicationPart(typeof(SampleOrdersController).Assembly);

        overrideServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseRouting();
        app.UseAppInHouseProjectContext();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        await app.StartAsync();
        return new AuthorizationTestHost(app);
    }

    /// <summary>Runs <paramref name="action"/> in a fresh scope (i.e. like a separate admin request).</summary>
    public async Task ScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
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

    public Task InvalidateUserAsync(string userId)
        => ScopeAsync(sp => sp.GetRequiredService<IPermissionInvalidationService>().InvalidateUserAsync(userId));

    public HttpRequestMessage Request(HttpMethod method, string url, string? userId, string? projectCode = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (userId is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userId);
        if (projectCode is not null)
            request.Headers.Add("X-Project-Code", projectCode);

        return request;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? userId, string? projectCode = null)
        => Client.SendAsync(Request(method, url, userId, projectCode));

    public Task<HttpResponseMessage> CreateOrderAsync(string? userId, string? projectCode)
        => SendAsync(HttpMethod.Post, "/api/orders", userId, projectCode);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(new[] { new Claim("sub", header["Bearer ".Length..]) }, AuthorizationTestHost.Scheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), AuthorizationTestHost.Scheme)));
        }
    }
}

/// <summary>Builds the entities of a scenario: two projects, an OrderManager role, users.</summary>
public sealed class Scenario
{
    public Project ProjectA { get; } = new("ProjectA", "Project A");
    public Project ProjectB { get; } = new("ProjectB", "Project B");

    public Permission OrdersCreateA { get; }
    public Permission OrdersCreateB { get; }
    public Permission OrdersView { get; } = new("orders.view", "View orders", "Orders"); // global

    public const string OrderManagerRoleId = "role-order-manager";

    public Scenario()
    {
        // Permission codes are unique platform-wide, so each project owns a differently-named one.
        OrdersCreateA = new Permission("orders.create", "Create orders", "Orders", ProjectA.Id);
        OrdersCreateB = new Permission("orders.create.b", "Create orders (B)", "Orders", ProjectB.Id);
    }

    public void Seed(AppIdentityDbContext identity, AppAuthDbContext auth)
    {
        identity.Roles.Add(new ApplicationRole("OrderManager") { Id = OrderManagerRoleId });
        identity.Roles.Add(new ApplicationRole("GlobalAdmin") { Id = "role-global-admin" });

        auth.Projects.AddRange(ProjectA, ProjectB);
        auth.Permissions.AddRange(OrdersCreateA, OrdersCreateB, OrdersView);
        auth.RolePermissions.Add(new RolePermission(OrderManagerRoleId, OrdersCreateA.Id));
    }

    public static ApplicationUser NewUser(string id, bool isActive = true, DateTimeOffset? lockoutEnd = null)
        => new() { Id = id, UserName = id, IsActive = isActive, LockoutEnd = lockoutEnd };

    /// <summary>The user holds OrderManager (orders.create) in Project A, nothing in Project B.</summary>
    public void GiveOrderManagerInProjectA(AppIdentityDbContext identity, AppAuthDbContext auth, string userId)
    {
        identity.Users.Add(NewUser(userId));
        auth.ProjectUserAssignments.Add(new ProjectUserAssignment(ProjectA.Id, userId));
        auth.UserRoleProjectAssignments.Add(new UserRoleProjectAssignment(userId, OrderManagerRoleId, ProjectA.Id));
    }
}
