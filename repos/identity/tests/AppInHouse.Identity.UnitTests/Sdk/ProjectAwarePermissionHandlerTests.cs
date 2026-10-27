using System.Security.Claims;
using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.ClientSdk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.Identity.UnitTests.Sdk;

public class ProjectAwarePermissionHandlerTests
{
    private readonly RecordingPermissionService _permissions = new();
    private readonly StubResolver _resolver = new() { ProjectCode = "ERP" };

    private ProjectAwarePermissionHandler CreateHandler(IHttpContextAccessor? accessor = null)
        => new(_permissions, _resolver, accessor ?? new HttpContextAccessor(), NullLogger<ProjectAwarePermissionHandler>.Instance);

    private static ClaimsPrincipal User(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static async Task<AuthorizationHandlerContext> RunAsync(
        ProjectAwarePermissionHandler handler, ClaimsPrincipal user, object? resource, string permission = "orders.create")
    {
        var requirement = new PermissionRequirement(permission);
        var context = new AuthorizationHandlerContext(new[] { requirement }, user, resource);
        await handler.HandleAsync(context);
        return context;
    }

    [Fact]
    public async Task Succeeds_WhenThePermissionServiceAllows()
    {
        _permissions.Allow = true;

        var context = await RunAsync(CreateHandler(), User(new Claim(ClaimTypes.NameIdentifier, "u1")), new DefaultHttpContext());

        Assert.True(context.HasSucceeded);
        Assert.Equal(("u1", "ERP", "orders.create"), _permissions.LastCall);
    }

    [Fact]
    public async Task Fails_WhenThePermissionServiceDenies()
    {
        _permissions.Allow = false;

        var context = await RunAsync(CreateHandler(), User(new Claim(ClaimTypes.NameIdentifier, "u1")), new DefaultHttpContext());

        Assert.True(context.HasFailed);
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task UsesTheSubClaim_WhenThereIsNoNameIdentifier()
    {
        _permissions.Allow = true;

        var context = await RunAsync(CreateHandler(), User(new Claim("sub", "sub-user")), new DefaultHttpContext());

        Assert.True(context.HasSucceeded);
        Assert.Equal("sub-user", _permissions.LastCall.UserId);
    }

    [Fact]
    public async Task Fails_WithoutASubject_AndNeverCallsTheService()
    {
        _permissions.Allow = true;

        var context = await RunAsync(CreateHandler(), User(), new DefaultHttpContext());

        Assert.True(context.HasFailed);
        Assert.Equal(0, _permissions.Calls);
    }

    [Fact]
    public async Task Fails_WithoutAProject_AndNeverCallsTheService()
    {
        _permissions.Allow = true;
        _resolver.ProjectCode = null;

        var context = await RunAsync(CreateHandler(), User(new Claim("sub", "u1")), new DefaultHttpContext());

        Assert.True(context.HasFailed);
        Assert.Equal(0, _permissions.Calls);
    }

    [Fact]
    public async Task Fails_WhenThePermissionServiceThrows()
    {
        _permissions.Throw = new InvalidOperationException("boom");

        var context = await RunAsync(CreateHandler(), User(new Claim("sub", "u1")), new DefaultHttpContext());

        Assert.True(context.HasFailed);
        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task PrefersTheProjectPublishedByTheMiddleware_OverResolvingAgain()
    {
        _permissions.Allow = true;
        _resolver.ProjectCode = "FROM-RESOLVER";
        var http = new DefaultHttpContext();
        http.Features.Set(new ProjectContext("FROM-MIDDLEWARE"));

        await RunAsync(CreateHandler(), User(new Claim("sub", "u1")), http);

        Assert.Equal("FROM-MIDDLEWARE", _permissions.LastCall.ProjectCode);
    }

    [Fact]
    public async Task AnInvalidProjectPublishedByTheMiddleware_IsNotReResolved()
    {
        _permissions.Allow = true;
        var http = new DefaultHttpContext();
        http.Features.Set(new ProjectContext(null));

        var context = await RunAsync(CreateHandler(), User(new Claim("sub", "u1")), http);

        Assert.True(context.HasFailed);
        Assert.Equal(0, _permissions.Calls);
    }

    [Fact]
    public async Task FallsBackToTheHttpContextAccessor_WhenTheResourceIsNotAnHttpContext()
    {
        _permissions.Allow = true;
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        var context = await RunAsync(CreateHandler(accessor), User(new Claim("sub", "u1")), resource: null);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_WhenNoHttpContextIsAvailableAtAll()
    {
        _permissions.Allow = true;

        var context = await RunAsync(CreateHandler(), User(new Claim("sub", "u1")), resource: null);

        Assert.True(context.HasFailed);
    }

    private sealed class StubResolver : IProjectContextResolver
    {
        public string? ProjectCode { get; set; }

        public string? Resolve(HttpContext httpContext) => ProjectCode;
    }

    private sealed class RecordingPermissionService : IPermissionService
    {
        public bool Allow { get; set; }
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        public (string UserId, string ProjectCode, string Permission) LastCall { get; private set; }

        public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(string userId, string projectCode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> HasPermissionAsync(string userId, string projectCode, string permission, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCall = (userId, projectCode, permission);
            return Throw is null ? Task.FromResult(Allow) : throw Throw;
        }
    }
}
