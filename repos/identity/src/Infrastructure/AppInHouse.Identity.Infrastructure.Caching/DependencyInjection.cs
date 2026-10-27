using AppInHouse.Identity.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.Identity.Infrastructure.Caching;

public static class DependencyInjection
{
    /// <summary>Registers the in-memory <see cref="IPermissionCacheService"/> (one instance per process).</summary>
    public static IServiceCollection AddPermissionCaching(
        this IServiceCollection services, Action<PermissionCacheOptions>? configure = null)
    {
        services.AddMemoryCache();
        services.AddOptions<PermissionCacheOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<IPermissionCacheService, PermissionCacheService>();
        return services;
    }
}
