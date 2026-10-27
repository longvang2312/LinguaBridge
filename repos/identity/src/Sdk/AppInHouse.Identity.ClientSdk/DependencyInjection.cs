using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppInHouse.Identity.ClientSdk;

public static class DependencyInjection
{
    /// <summary>
    /// Adds <c>[HasPermission]</c> support. The host must also provide an <c>IPermissionService</c>
    /// (for example via <c>AddAuthorizationEngine()</c>) and an authentication scheme.
    /// </summary>
    public static IServiceCollection AddAppInHouseAuthorization(
        this IServiceCollection services, Action<AppInHouseAuthorizationOptions>? configure = null)
    {
        services.AddOptions<AppInHouseAuthorizationOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpContextAccessor();
        services.AddAuthorization();

        services.TryAddSingleton<IProjectContextResolver, ProjectContextResolver>();
        services.AddSingleton<IAuthorizationPolicyProvider, DynamicPermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, ProjectAwarePermissionHandler>();

        return services;
    }

    /// <summary>Resolves the request's project. Call after <c>UseRouting()</c> and before <c>UseAuthorization()</c>.</summary>
    public static IApplicationBuilder UseAppInHouseProjectContext(this IApplicationBuilder app)
        => app.UseMiddleware<ProjectContextMiddleware>();
}
