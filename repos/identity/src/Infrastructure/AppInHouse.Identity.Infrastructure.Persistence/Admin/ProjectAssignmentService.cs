using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Application.Authorization;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

public sealed class ProjectAssignmentService : IProjectAssignmentService
{
    private readonly AppAuthDbContext _auth;
    private readonly AppIdentityDbContext _identity;
    private readonly IAdminAuthorizer _authorizer;
    private readonly IAuditService _audit;
    private readonly IPermissionInvalidationService _invalidation;

    public ProjectAssignmentService(
        AppAuthDbContext auth,
        AppIdentityDbContext identity,
        IAdminAuthorizer authorizer,
        IAuditService audit,
        IPermissionInvalidationService invalidation)
    {
        _auth = auth;
        _identity = identity;
        _authorizer = authorizer;
        _audit = audit;
        _invalidation = invalidation;
    }

    public async Task<ProjectUserDto> AssignUserAsync(
        Guid projectId, AssignProjectUserRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireProjectAdmin(projectId);
        await EnsureProjectExistsAsync(projectId, cancellationToken);

        var userId = request.UserId.Trim();
        if (!await _identity.Users.AnyAsync(u => u.Id == userId, cancellationToken))
            throw new NotFoundException("The user was not found.");

        var assignment = await _auth.ProjectUserAssignments
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.UserId == userId, cancellationToken);

        string action;
        object? before = null;
        if (assignment is null)
        {
            // Anti privilege escalation: project administrators cannot appoint (other) project administrators.
            if (request.IsProjectAdmin)
                RequireGlobalAdminForAdminFlag(caller);

            assignment = new ProjectUserAssignment(projectId, userId, request.IsProjectAdmin);
            _auth.ProjectUserAssignments.Add(assignment);
            action = AuditActions.Assign;
        }
        else if (assignment.IsProjectAdmin != request.IsProjectAdmin)
        {
            RequireGlobalAdminForAdminFlag(caller);

            before = Snapshot(assignment);
            assignment.SetProjectAdmin(request.IsProjectAdmin);
            action = AuditActions.Update;
        }
        else
        {
            return ToDto(assignment); // nothing to change
        }

        await _auth.SaveChangesAsync(cancellationToken);

        await _invalidation.InvalidateUserAsync(userId, cancellationToken);
        await _audit.WriteAsync(
            new AuditEntry(action, AuditEntities.ProjectUser, $"{projectId}:{userId}", projectId, before, Snapshot(assignment)),
            cancellationToken);

        return ToDto(assignment);
    }

    public async Task<UserRolesDto> SetUserRolesAsync(
        Guid projectId, string userId, SetUserRolesRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireProjectAdmin(projectId);
        await EnsureProjectExistsAsync(projectId, cancellationToken);
        await EnsureMemberAsync(projectId, userId, cancellationToken);

        var desired = request.RoleIds.Select(r => r?.Trim()).Where(r => !string.IsNullOrEmpty(r)).Cast<string>().ToHashSet();
        if (desired.Count != request.RoleIds.Count)
            throw new BadRequestException("RoleIds must be non-empty and unique.");

        var roles = await _identity.Roles.AsNoTracking()
            .Where(r => desired.Contains(r.Id))
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(cancellationToken);

        var missing = desired.Except(roles.Select(r => r.Id)).ToList();
        if (missing.Count > 0)
            throw new BadRequestException($"Unknown role id(s): {string.Join(", ", missing)}.");

        // The platform role is never granted through a project.
        if (roles.Any(r => string.Equals(r.Name, AuthorizationConstants.GlobalAdminRole, StringComparison.OrdinalIgnoreCase)))
            throw new BadRequestException($"The '{AuthorizationConstants.GlobalAdminRole}' role cannot be assigned within a project.");

        var current = await _auth.UserRoleProjectAssignments
            .Where(a => a.ProjectId == projectId && a.UserId == userId)
            .ToListAsync(cancellationToken);
        var before = current.Select(a => a.RoleId).Order().ToList();

        _auth.UserRoleProjectAssignments.RemoveRange(current.Where(a => !desired.Contains(a.RoleId)));
        foreach (var roleId in desired.Except(current.Select(a => a.RoleId)))
            _auth.UserRoleProjectAssignments.Add(new UserRoleProjectAssignment(userId, roleId, projectId));

        await _auth.SaveChangesAsync(cancellationToken);

        await _invalidation.InvalidateUserAsync(userId, cancellationToken);
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.SetRoles, AuditEntities.UserRole, $"{projectId}:{userId}", projectId,
                new { roleIds = before }, new { roleIds = desired.Order().ToList() }),
            cancellationToken);

        return new UserRolesDto(projectId, userId, desired.Order().ToList());
    }

    public async Task<UserPermissionsDto> SetUserPermissionsAsync(
        Guid projectId, string userId, SetUserPermissionsRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireProjectAdmin(projectId);
        await EnsureProjectExistsAsync(projectId, cancellationToken);
        await EnsureMemberAsync(projectId, userId, cancellationToken);

        if (request.Assignments.Any(a => a is null || a.PermissionId is null))
            throw new BadRequestException("Every assignment needs a permissionId.");

        var ids = request.Assignments.Select(a => a.PermissionId!.Value).ToList();
        if (ids.Distinct().Count() != ids.Count)
            throw new BadRequestException("A permission can appear only once.");

        var desired = request.Assignments.ToDictionary(a => a.PermissionId!.Value, a => a.IsRevoked);

        var permissions = await _auth.Permissions.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.PermissionCode, p.IsGlobal, p.ProjectId })
            .ToListAsync(cancellationToken);

        var missing = desired.Keys.Except(permissions.Select(p => p.Id)).ToList();
        if (missing.Count > 0)
            throw new BadRequestException($"Unknown permission id(s): {string.Join(", ", missing)}.");

        // A permission of another project can never apply here (and must not be smuggled in through this project).
        var foreign = permissions.Where(p => !p.IsGlobal && p.ProjectId != projectId).Select(p => p.PermissionCode).ToList();
        if (foreign.Count > 0)
            throw new BadRequestException($"Permission(s) {string.Join(", ", foreign)} do not belong to this project.");

        var current = await _auth.UserPermissionAssignments
            .Where(a => a.ProjectId == projectId && a.UserId == userId)
            .Include(a => a.Permission)
            .ToListAsync(cancellationToken);
        var before = current.OrderBy(a => a.Permission.PermissionCode)
            .Select(a => new { permission = a.Permission.PermissionCode, isRevoked = a.IsRevoked }).ToList();

        // Replace the set: an assignment whose revoke flag changed is recreated.
        _auth.UserPermissionAssignments.RemoveRange(
            current.Where(a => !desired.TryGetValue(a.PermissionId, out var revoked) || revoked != a.IsRevoked));
        foreach (var (permissionId, isRevoked) in desired)
        {
            if (!current.Any(a => a.PermissionId == permissionId && a.IsRevoked == isRevoked))
                _auth.UserPermissionAssignments.Add(new UserPermissionAssignment(userId, permissionId, projectId, isRevoked));
        }

        await _auth.SaveChangesAsync(cancellationToken);

        await _invalidation.InvalidateUserAsync(userId, cancellationToken);

        var result = permissions
            .OrderBy(p => p.PermissionCode)
            .Select(p => new DirectPermissionDto(p.Id, p.PermissionCode, desired[p.Id]))
            .ToList();
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.SetPermissions, AuditEntities.UserPermission, $"{projectId}:{userId}", projectId,
                new { assignments = before },
                new { assignments = result.Select(r => new { permission = r.PermissionCode, r.IsRevoked }).ToList() }),
            cancellationToken);

        return new UserPermissionsDto(projectId, userId, result);
    }

    private async Task EnsureProjectExistsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await _auth.Projects.AnyAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken))
            throw new NotFoundException("The project was not found.");
    }

    private async Task EnsureMemberAsync(Guid projectId, string userId, CancellationToken cancellationToken)
    {
        if (!await _auth.ProjectUserAssignments.AnyAsync(a => a.ProjectId == projectId && a.UserId == userId, cancellationToken))
            throw new BadRequestException("The user is not assigned to the project. Assign the user first.");
    }

    private static void RequireGlobalAdminForAdminFlag(AdminCaller caller)
    {
        if (!caller.IsGlobalAdmin)
            throw new ForbiddenException("Only a global administrator can grant or change project administrator rights.");
    }

    private static ProjectUserDto ToDto(ProjectUserAssignment a)
        => new(a.ProjectId, a.UserId, a.IsProjectAdmin, a.IsBlocked, a.AssignedAt);

    private static object Snapshot(ProjectUserAssignment a) => new { a.ProjectId, a.UserId, a.IsProjectAdmin, a.IsBlocked };
}
