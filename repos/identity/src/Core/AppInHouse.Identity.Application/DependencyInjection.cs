using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.Identity.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the dynamic authorization engine. The host must also register <see cref="IPermissionDataSource"/> and
    /// <see cref="IPermissionVersionStore"/> (persistence) and <see cref="IPermissionCacheService"/> (caching).
    /// </summary>
    public static IServiceCollection AddAuthorizationEngine(this IServiceCollection services)
    {
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IPermissionInvalidationService, PermissionInvalidationService>();
        return services;
    }
}
