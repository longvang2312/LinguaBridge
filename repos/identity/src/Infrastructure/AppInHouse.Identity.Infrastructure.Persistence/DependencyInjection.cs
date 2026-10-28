using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Clients;
using AppInHouse.Identity.Application.Admin.Permissions;
using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Roles;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Application.Admin.Users;
using AppInHouse.Identity.Infrastructure.Persistence.Admin;
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

    /// <summary>
    /// Registers the admin use cases. Requires the three DbContexts, ASP.NET Core Identity (<c>UserManager</c>,
    /// <c>RoleManager</c>), the OpenIddict managers, <see cref="ICurrentCaller"/>, the authorization engine
    /// (<c>AddAuthorizationEngine</c>, <c>AddPermissionPersistence</c>, a cache) and <c>AddAdminAuthorization</c>.
    /// </summary>
    public static IServiceCollection AddAdminServices(this IServiceCollection services)
    {
        services.AddScoped<IAdminAccessReader, EfAdminAccessReader>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();
        services.AddScoped<IProjectAdminService, ProjectAdminService>();
        services.AddScoped<IProjectAssignmentService, ProjectAssignmentService>();
        services.AddScoped<IPermissionAdminService, PermissionAdminService>();
        services.AddScoped<IRoleAdminService, RoleAdminService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IClientAdminService, ClientAdminService>();
        return services;
    }
}
