using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Admin.Security;
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

    /// <summary>
    /// Registers the caller authorization of the admin use cases. The host must provide
    /// <see cref="ICurrentCaller"/>; the persistence layer provides <see cref="IAdminAccessReader"/> and the use cases.
    /// </summary>
    public static IServiceCollection AddAdminAuthorization(this IServiceCollection services)
    {
        services.AddScoped<IAdminAuthorizer, AdminAuthorizer>();
        return services;
    }
}
