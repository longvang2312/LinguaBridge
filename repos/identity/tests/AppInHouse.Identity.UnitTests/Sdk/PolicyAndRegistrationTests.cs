using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.ClientSdk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.UnitTests.Sdk;

public class PolicyAndRegistrationTests
{
    // ---- [HasPermission] ----

    [Fact]
    public void Attribute_MapsToThePermissionPolicy()
    {
        var attribute = new HasPermissionAttribute("users.view");

        Assert.Equal("PERM:users.view", attribute.Policy);
        Assert.Equal("users.view", attribute.Permission);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Attribute_RejectsBlankPermissions(string permission)
        => Assert.ThrowsAny<ArgumentException>(() => new HasPermissionAttribute(permission));

    [Fact]
    public void Attribute_CanBeAppliedToClassesAndMethods_AndRepeated()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(typeof(HasPermissionAttribute), typeof(AttributeUsageAttribute))!;

        Assert.True(usage.AllowMultiple);
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Class));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Method));
        Assert.IsAssignableFrom<AuthorizeAttribute>(new HasPermissionAttribute("a.b"));
    }

    // ---- Policy provider ----

    [Fact]
    public async Task PolicyProvider_BuildsAPermissionPolicy_RequiringAnAuthenticatedUser()
    {
        var provider = new DynamicPermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync("PERM:orders.create");

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
        var requirement = Assert.Single(policy.Requirements.OfType<PermissionRequirement>());
        Assert.Equal("orders.create", requirement.Permission);
    }

    [Fact]
    public async Task PolicyProvider_DelegatesOtherPolicyNamesToTheDefaultProvider()
    {
        var options = new AuthorizationOptions();
        options.AddPolicy("Custom", p => p.RequireAuthenticatedUser());
        var provider = new DynamicPermissionPolicyProvider(Options.Create(options));

        Assert.NotNull(await provider.GetPolicyAsync("Custom"));
        Assert.Null(await provider.GetPolicyAsync("Unknown"));
        Assert.Null(await provider.GetPolicyAsync("PERM:"));
    }

    // ---- Middleware ----

    [Fact]
    public async Task Middleware_PublishesTheResolvedProject_AndContinues()
    {
        var called = false;
        var middleware = new ProjectContextMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var http = new DefaultHttpContext();

        await middleware.InvokeAsync(http, new FixedResolver("ERP"));

        Assert.True(called);
        Assert.Equal("ERP", http.Features.Get<ProjectContext>()!.ProjectCode);
    }

    [Fact]
    public async Task Middleware_PublishesNull_WhenTheProjectCannotBeResolved()
    {
        var http = new DefaultHttpContext();

        await new ProjectContextMiddleware(_ => Task.CompletedTask).InvokeAsync(http, new FixedResolver(null));

        Assert.Null(http.Features.Get<ProjectContext>()!.ProjectCode);
    }

    // ---- DI ----

    [Fact]
    public void AddAppInHouseAuthorization_RegistersTheSdkServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPermissionService, NeverCalledPermissionService>();
        services.AddAppInHouseAuthorization(o => o.ProjectCodeHeaderName = "X-Tenant");

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<DynamicPermissionPolicyProvider>(provider.GetRequiredService<IAuthorizationPolicyProvider>());
        Assert.IsType<ProjectContextResolver>(provider.GetRequiredService<IProjectContextResolver>());
        Assert.Equal("X-Tenant", provider.GetRequiredService<IOptions<AppInHouseAuthorizationOptions>>().Value.ProjectCodeHeaderName);
        Assert.Contains(scope.ServiceProvider.GetServices<IAuthorizationHandler>(), h => h is ProjectAwarePermissionHandler);
    }

    private sealed class FixedResolver(string? code) : IProjectContextResolver
    {
        public string? Resolve(HttpContext httpContext) => code;
    }

    private sealed class NeverCalledPermissionService : IPermissionService
    {
        public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(string userId, string projectCode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> HasPermissionAsync(string userId, string projectCode, string permission, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
