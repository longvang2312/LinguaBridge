using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Infrastructure.Persistence.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.Identity.Infrastructure.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the EF Core backed authorization data access. Requires <c>AppIdentityDbContext</c> and
    /// <c>AppAuthDbContext</c> to be registered by the host.
    /// </summary>
    public static IServiceCollection AddPermissionPersistence(this IServiceCollection services)
    {
        services.AddScoped<IPermissionDataSource, EfPermissionDataSource>();
        services.AddScoped<IPermissionVersionStore, PermissionVersionStore>();
        return services;
    }
}
