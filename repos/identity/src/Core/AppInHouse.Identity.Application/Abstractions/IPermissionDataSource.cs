using AppInHouse.Identity.Application.Authorization;

namespace AppInHouse.Identity.Application.Abstractions;

/// <summary>
/// Read access to the authorization data. Implemented by the persistence layer so the application layer
/// stays free of EF Core / ASP.NET Core Identity.
/// </summary>
/// <remarks>
/// "Applicable" permissions are active permissions that are either global or belong to the given project;
/// a permission of another project never applies, even when a shared role contains it.
/// </remarks>
public interface IPermissionDataSource
{
    /// <returns>Null when the user does not exist.</returns>
    Task<UserAccessState?> GetUserAccessStateAsync(string userId, CancellationToken cancellationToken = default);

    Task<bool> IsGlobalAdminAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Finds the project by code, preferring a non-deleted one. Null when there is none.</summary>
    Task<ProjectAccessInfo?> FindProjectAsync(string projectCode, CancellationToken cancellationToken = default);

    Task<ProjectMembership?> GetProjectMembershipAsync(Guid projectId, string userId, CancellationToken cancellationToken = default);

    /// <summary>All applicable permission codes of the project (project-specific + global).</summary>
    Task<IReadOnlyCollection<string>> GetAllPermissionCodesAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Applicable permission codes granted by the roles the user holds in the project.</summary>
    Task<IReadOnlyCollection<string>> GetRolePermissionCodesAsync(string userId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Direct assignments of the user in the project: every revocation, and only the grants of applicable permissions.
    /// </summary>
    Task<IReadOnlyCollection<DirectPermissionAssignment>> GetDirectPermissionsAsync(string userId, Guid projectId, CancellationToken cancellationToken = default);
}
