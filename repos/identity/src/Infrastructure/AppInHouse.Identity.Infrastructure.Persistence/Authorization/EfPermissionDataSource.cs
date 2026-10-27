using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Authorization;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Authorization;

/// <summary>
/// EF Core implementation of <see cref="IPermissionDataSource"/>. Users and roles live in
/// <see cref="AppIdentityDbContext"/>, everything else in <see cref="AppAuthDbContext"/>.
/// All queries are read-only (no tracking).
/// </summary>
public class EfPermissionDataSource : IPermissionDataSource
{
    private readonly AppIdentityDbContext _identity;
    private readonly AppAuthDbContext _auth;

    public EfPermissionDataSource(AppIdentityDbContext identity, AppAuthDbContext auth)
    {
        _identity = identity;
        _auth = auth;
    }

    public Task<UserAccessState?> GetUserAccessStateAsync(string userId, CancellationToken cancellationToken = default)
        => _identity.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserAccessState(u.IsActive, u.LockoutEnd))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> IsGlobalAdminAsync(string userId, CancellationToken cancellationToken = default)
        => (from userRole in _identity.UserRoles.AsNoTracking()
            join role in _identity.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            where userRole.UserId == userId && role.Name == AuthorizationConstants.GlobalAdminRole
            select userRole.UserId)
            .AnyAsync(cancellationToken);

    public Task<ProjectAccessInfo?> FindProjectAsync(string projectCode, CancellationToken cancellationToken = default)
        // A deleted project's code can be reused (filtered unique index), so prefer the live one.
        => _auth.Projects.AsNoTracking()
            .Where(p => p.ProjectCode == projectCode)
            .OrderBy(p => p.IsDeleted)
            .Select(p => new ProjectAccessInfo(p.Id, p.IsActive, p.IsDeleted))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ProjectMembership?> GetProjectMembershipAsync(Guid projectId, string userId, CancellationToken cancellationToken = default)
        => _auth.ProjectUserAssignments.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.UserId == userId)
            .Select(a => new ProjectMembership(a.IsProjectAdmin, a.IsBlocked))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyCollection<string>> GetAllPermissionCodesAsync(Guid projectId, CancellationToken cancellationToken = default)
        => await _auth.Permissions.AsNoTracking()
            .Where(p => p.IsActive && (p.IsGlobal || p.ProjectId == projectId))
            .Select(p => p.PermissionCode)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<string>> GetRolePermissionCodesAsync(string userId, Guid projectId, CancellationToken cancellationToken = default)
        => await (from assignment in _auth.UserRoleProjectAssignments.AsNoTracking()
                  join rolePermission in _auth.RolePermissions.AsNoTracking() on assignment.RoleId equals rolePermission.RoleId
                  join permission in _auth.Permissions.AsNoTracking() on rolePermission.PermissionId equals permission.Id
                  where assignment.UserId == userId
                        && assignment.ProjectId == projectId
                        && permission.IsActive
                        && (permission.IsGlobal || permission.ProjectId == projectId)
                  select permission.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<DirectPermissionAssignment>> GetDirectPermissionsAsync(string userId, Guid projectId, CancellationToken cancellationToken = default)
        => await _auth.UserPermissionAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.ProjectId == projectId)
            // Revocations always apply; a grant only counts when the permission is active and applicable to the project.
            .Where(a => a.IsRevoked
                        || (a.Permission.IsActive && (a.Permission.IsGlobal || a.Permission.ProjectId == projectId)))
            .Select(a => new DirectPermissionAssignment(a.Permission.PermissionCode, a.IsRevoked))
            .ToListAsync(cancellationToken);
}
