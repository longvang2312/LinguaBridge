using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Permissions;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

public sealed class PermissionAdminService : IPermissionAdminService
{
    private readonly AppAuthDbContext _auth;
    private readonly IAdminAuthorizer _authorizer;
    private readonly IAuditService _audit;
    private readonly IPermissionInvalidationService _invalidation;

    public PermissionAdminService(
        AppAuthDbContext auth,
        IAdminAuthorizer authorizer,
        IAuditService audit,
        IPermissionInvalidationService invalidation)
    {
        _auth = auth;
        _authorizer = authorizer;
        _audit = audit;
        _invalidation = invalidation;
    }

    public async Task<PagedResult<PermissionDto>> ListAsync(PermissionListQuery query, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireAnyAdmin();

        var permissions = _auth.Permissions.AsNoTracking();

        if (query.ProjectId is { } projectId)
        {
            caller.RequireProjectAdmin(projectId);

            // The permissions that can apply to the project: its own and the global ones.
            permissions = permissions.Where(p => p.IsGlobal || p.ProjectId == projectId);
        }
        else if (!caller.IsGlobalAdmin)
        {
            // Project administrators see the global permissions and those of the projects they administer.
            var visible = caller.AdministeredProjectIds.ToList();
            permissions = permissions.Where(p => p.IsGlobal || (p.ProjectId != null && visible.Contains(p.ProjectId.Value)));
        }

        if (!string.IsNullOrWhiteSpace(query.Module))
        {
            var module = query.Module.Trim();
            permissions = permissions.Where(p => p.Module == module);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            permissions = permissions.Where(p => p.PermissionCode.Contains(search) || p.DisplayName.Contains(search));
        }

        if (query.IsActive is { } isActive)
            permissions = permissions.Where(p => p.IsActive == isActive);

        return await permissions
            .OrderBy(p => p.PermissionCode)
            .Select(p => new PermissionDto(p.Id, p.ProjectId, p.PermissionCode, p.DisplayName, p.Module, p.IsGlobal, p.IsActive, p.CreatedAt))
            .ToPagedResultAsync(query, cancellationToken);
    }

    public async Task<PermissionDto> CreateAsync(CreatePermissionRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);

        // Global permissions are platform-wide; project permissions may be defined by that project's administrators only.
        if (request.ProjectId is { } projectId)
        {
            caller.RequireProjectAdmin(projectId);
            if (!await _auth.Projects.AnyAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken))
                throw new NotFoundException("The project was not found.");
        }
        else
        {
            caller.RequireGlobalAdmin();
        }

        var permission = new Permission(request.PermissionCode, request.DisplayName, request.Module, request.ProjectId);

        var code = permission.PermissionCode.ToUpper();
        if (await _auth.Permissions.AnyAsync(p => p.PermissionCode.ToUpper() == code, cancellationToken))
            throw new ConflictException($"A permission with the code '{permission.PermissionCode}' already exists.");

        _auth.Permissions.Add(permission);
        try
        {
            await _auth.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException($"A permission with the code '{permission.PermissionCode}' already exists.");
        }

        // A brand new permission is held by nobody yet, so there is nothing to invalidate.
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Create, AuditEntities.Permission, permission.Id.ToString(), permission.ProjectId, NewValues: Snapshot(permission)),
            cancellationToken);

        return ToDto(permission);
    }

    public async Task<PermissionDto> UpdateAsync(Guid id, UpdatePermissionRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireAnyAdmin();

        if (request.IsActive is null)
            throw new BadRequestException("IsActive is required.");

        var permission = await _auth.Permissions.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (permission is null)
        {
            // Do not tell non-global callers which ids exist.
            throw caller.IsGlobalAdmin ? new NotFoundException("The permission was not found.") : new ForbiddenException();
        }

        if (permission.ProjectId is { } owner)
            caller.RequireProjectAdmin(owner);
        else
            caller.RequireGlobalAdmin();

        var displayName = request.DisplayName.Trim();
        var module = request.Module.Trim();
        if (displayName.Length == 0 || module.Length == 0)
            throw new BadRequestException("DisplayName and Module must not be blank.");

        var before = Snapshot(permission);
        var activeChanged = permission.IsActive != request.IsActive;

        // The domain entity exposes no setters for these descriptive fields; EF writes them via the property entries.
        var entry = _auth.Entry(permission);
        entry.Property(p => p.DisplayName).CurrentValue = displayName;
        entry.Property(p => p.Module).CurrentValue = module;
        if (request.IsActive == true)
            permission.Activate();
        else
            permission.Deactivate();

        await _auth.SaveChangesAsync(cancellationToken);

        if (activeChanged)
            await InvalidateAsync(permission, cancellationToken);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Update, AuditEntities.Permission, permission.Id.ToString(), permission.ProjectId, before, Snapshot(permission)),
            cancellationToken);

        return ToDto(permission);
    }

    /// <summary>(De)activating a permission changes the effective permissions of everyone who holds it.</summary>
    private async Task InvalidateAsync(Permission permission, CancellationToken cancellationToken)
    {
        if (permission.ProjectId is not { } projectId)
        {
            await _invalidation.InvalidateAllAsync(cancellationToken);
            return;
        }

        var code = await _auth.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.ProjectCode)
            .FirstAsync(cancellationToken);
        await _invalidation.InvalidateProjectAsync(code, cancellationToken);
    }

    private static PermissionDto ToDto(Permission p)
        => new(p.Id, p.ProjectId, p.PermissionCode, p.DisplayName, p.Module, p.IsGlobal, p.IsActive, p.CreatedAt);

    private static object Snapshot(Permission p)
        => new { p.Id, p.ProjectId, p.PermissionCode, p.DisplayName, p.Module, p.IsGlobal, p.IsActive };
}
