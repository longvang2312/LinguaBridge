using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Roles;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Application.Authorization;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

public sealed class RoleAdminService : IRoleAdminService
{
    private readonly AppIdentityDbContext _identity;
    private readonly AppAuthDbContext _auth;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IAdminAuthorizer _authorizer;
    private readonly IAuditService _audit;
    private readonly IPermissionInvalidationService _invalidation;

    public RoleAdminService(
        AppIdentityDbContext identity,
        AppAuthDbContext auth,
        RoleManager<ApplicationRole> roleManager,
        IAdminAuthorizer authorizer,
        IAuditService audit,
        IPermissionInvalidationService invalidation)
    {
        _identity = identity;
        _auth = auth;
        _roleManager = roleManager;
        _authorizer = authorizer;
        _audit = audit;
        _invalidation = invalidation;
    }

    public async Task<PagedResult<RoleDto>> ListAsync(RoleListQuery query, CancellationToken cancellationToken = default)
    {
        // Project administrators need the role list to assign roles in their project.
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireAnyAdmin();

        var roles = _identity.Roles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            roles = roles.Where(r => r.Name!.Contains(search));
        }

        var page = await roles.OrderBy(r => r.Name).ToPagedResultAsync(query, cancellationToken);

        // Which permissions a role carries is only disclosed to global administrators.
        var permissions = caller.IsGlobalAdmin
            ? await LoadPermissionsAsync(page.Items.Select(r => r.Id).ToList(), cancellationToken)
            : null;

        var items = page.Items
            .Select(r => ToDto(r, permissions is null ? null : permissions.GetValueOrDefault(r.Id) ?? new List<RolePermissionDto>()))
            .ToList();
        return new PagedResult<RoleDto>(items, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<RoleDto> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var role = new ApplicationRole(request.Name.Trim()) { Description = request.Description?.Trim() };
        IdentityResultMapper.ThrowIfFailed(await _roleManager.CreateAsync(role));

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Create, AuditEntities.Role, role.Id, NewValues: Snapshot(role)),
            cancellationToken);

        return ToDto(role, new List<RolePermissionDto>());
    }

    public async Task<RoleDto> UpdateAsync(string id, UpdateRoleRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var role = await _identity.Roles.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("The role was not found.");

        var newName = request.Name.Trim();
        var isPlatformRole = string.Equals(role.Name, AuthorizationConstants.GlobalAdminRole, StringComparison.OrdinalIgnoreCase);
        if (isPlatformRole && !string.Equals(role.Name, newName, StringComparison.Ordinal))
            throw new BadRequestException($"The '{AuthorizationConstants.GlobalAdminRole}' role cannot be renamed.");
        if (!isPlatformRole && string.Equals(newName, AuthorizationConstants.GlobalAdminRole, StringComparison.OrdinalIgnoreCase))
            throw new ConflictException($"The name '{AuthorizationConstants.GlobalAdminRole}' is reserved.");

        var before = Snapshot(role);
        role.Name = newName;
        role.Description = request.Description?.Trim();
        IdentityResultMapper.ThrowIfFailed(await _roleManager.UpdateAsync(role));

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Update, AuditEntities.Role, role.Id, OldValues: before, NewValues: Snapshot(role)),
            cancellationToken);

        var permissions = await LoadPermissionsAsync(new List<string> { role.Id }, cancellationToken);
        return ToDto(role, permissions.GetValueOrDefault(role.Id) ?? new List<RolePermissionDto>());
    }

    public async Task<RoleDto> SetPermissionsAsync(string id, SetRolePermissionsRequest request, CancellationToken cancellationToken = default)
    {
        // Changing what a role grants affects every holder in every project: platform decision.
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var role = await _identity.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("The role was not found.");

        var desired = request.PermissionIds.Distinct().ToList();
        var permissions = await _auth.Permissions.AsNoTracking()
            .Where(p => desired.Contains(p.Id))
            .Select(p => new { p.Id, p.PermissionCode })
            .ToListAsync(cancellationToken);

        var missing = desired.Except(permissions.Select(p => p.Id)).ToList();
        if (missing.Count > 0)
            throw new BadRequestException($"Unknown permission id(s): {string.Join(", ", missing)}.");

        var current = await _auth.RolePermissions.Where(rp => rp.RoleId == id).Include(rp => rp.Permission).ToListAsync(cancellationToken);
        var before = current.Select(rp => rp.Permission.PermissionCode).Order().ToList();

        _auth.RolePermissions.RemoveRange(current.Where(rp => !desired.Contains(rp.PermissionId)));
        foreach (var permissionId in desired.Except(current.Select(rp => rp.PermissionId)))
            _auth.RolePermissions.Add(new RolePermission(id, permissionId));

        await _auth.SaveChangesAsync(cancellationToken);

        // Mandatory: the role may be held by any user in any project, so every cached permission set is stale now.
        await _invalidation.InvalidateAllAsync(cancellationToken);

        var result = permissions
            .OrderBy(p => p.PermissionCode)
            .Select(p => new RolePermissionDto(p.Id, p.PermissionCode))
            .ToList();
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.SetPermissions, AuditEntities.Role, role.Id,
                OldValues: new { permissions = before },
                NewValues: new { permissions = result.Select(r => r.PermissionCode).ToList() }),
            cancellationToken);

        return ToDto(role, result);
    }

    private async Task<Dictionary<string, List<RolePermissionDto>>> LoadPermissionsAsync(
        IReadOnlyCollection<string> roleIds, CancellationToken cancellationToken)
    {
        var rows = await _auth.RolePermissions.AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => new { rp.RoleId, rp.PermissionId, rp.Permission.PermissionCode })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.RoleId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.PermissionCode).Select(r => new RolePermissionDto(r.PermissionId, r.PermissionCode)).ToList());
    }

    private static RoleDto ToDto(ApplicationRole role, IReadOnlyList<RolePermissionDto>? permissions)
        => new(role.Id, role.Name ?? string.Empty, role.Description, role.CreatedAt, permissions);

    private static object Snapshot(ApplicationRole role) => new { role.Id, role.Name, role.Description };
}
