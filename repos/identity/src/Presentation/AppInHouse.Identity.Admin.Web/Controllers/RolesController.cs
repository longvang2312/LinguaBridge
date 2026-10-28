using AppInHouse.Identity.Admin.Web.Infrastructure;
using AppInHouse.Identity.Admin.Web.ViewModels;
using AppInHouse.Identity.Application.Admin.Permissions;
using AppInHouse.Identity.Application.Admin.Roles;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Application.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Controllers;

public class RolesController : PortalController
{
    private readonly IRoleAdminService _roles;
    private readonly IPermissionAdminService _permissions;
    private readonly IAdminAuthorizer _authorizer;

    public RolesController(IRoleAdminService roles, IPermissionAdminService permissions, IAdminAuthorizer authorizer)
    {
        _roles = roles;
        _permissions = permissions;
        _authorizer = authorizer;
    }

    public async Task<IActionResult> Index(RoleListQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            query = new RoleListQuery();

        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        var result = await _roles.ListAsync(query, cancellationToken);
        return View(new RoleListViewModel(query, result.Items, PagerModel.From(result), caller.IsGlobalAdmin));
    }

    [HttpGet]
    public IActionResult Create() => View(new CreateRoleRequest());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateRoleRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(request);

        var role = await _roles.CreateAsync(request, cancellationToken);
        Success($"Role {role.Name} was created. Grant it permissions in the matrix.");
        return RedirectToAction(nameof(Matrix));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var role = await FindAsync(id, cancellationToken);
        ViewData["RoleId"] = role.Id;
        return View(new UpdateRoleRequest { Name = role.Name, Description = role.Description });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            ViewData["RoleId"] = id;
            return View(request);
        }

        var role = await _roles.UpdateAsync(id, request, cancellationToken);
        Success($"Role {role.Name} was updated.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>The RBAC matrix: which role carries which permission. Global administrators only.</summary>
    public async Task<IActionResult> Matrix(CancellationToken cancellationToken)
        => View(await LoadMatrixAsync(cancellationToken));

    /// <param name="cells">The checked cells, as <c>{roleId}|{permissionId}</c>.</param>
    /// <param name="roleIds">The roles shown by the page; roles that are not listed are left untouched.</param>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Matrix(List<string>? cells, List<string>? roleIds, CancellationToken cancellationToken)
    {
        var matrix = await LoadMatrixAsync(cancellationToken);
        var checkedCells = (cells ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
        var shown = (roleIds ?? new List<string>()).ToHashSet(StringComparer.Ordinal);

        var changed = new List<string>();
        foreach (var role in matrix.Roles.Where(r => shown.Contains(r.Id)))
        {
            var current = (role.Permissions ?? Array.Empty<RolePermissionDto>()).Select(p => p.PermissionId).ToHashSet();
            var desired = matrix.Permissions.Where(p => checkedCells.Contains($"{role.Id}|{p.Id}")).Select(p => p.Id).ToHashSet();

            // Permissions the matrix does not show (deactivated ones) keep whatever the role has.
            desired.UnionWith(current.Except(matrix.Permissions.Select(p => p.Id)));
            if (desired.SetEquals(current))
                continue;

            // Each change invalidates every cached permission set, so untouched roles are not written at all.
            await _roles.SetPermissionsAsync(role.Id, new SetRolePermissionsRequest { PermissionIds = desired.ToList() }, cancellationToken);
            changed.Add(role.Name);
        }

        if (changed.Count == 0)
            Success("Nothing to save: no permission assignment changed.");
        else
            Success($"Permissions saved for: {string.Join(", ", changed)}. The change applies immediately.");

        return RedirectToAction(nameof(Matrix));
    }

    private async Task<RoleMatrixViewModel> LoadMatrixAsync(CancellationToken cancellationToken)
    {
        (await _authorizer.GetCallerAsync(cancellationToken)).RequireGlobalAdmin();

        // GlobalAdmin has every right by definition; it is not edited here.
        var roles = (await AllAsync(page => _roles.ListAsync(new RoleListQuery { Page = page, PageSize = 100 }, cancellationToken)))
            .Where(r => !string.Equals(r.Name, AuthorizationConstants.GlobalAdminRole, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var permissions = await AllAsync(page => _permissions.ListAsync(
            new PermissionListQuery { IsActive = true, Page = page, PageSize = 100 }, cancellationToken));

        return new RoleMatrixViewModel(roles, permissions);
    }

    private async Task<RoleDto> FindAsync(string id, CancellationToken cancellationToken)
    {
        var roles = await AllAsync(page => _roles.ListAsync(new RoleListQuery { Page = page, PageSize = 100 }, cancellationToken));
        return roles.FirstOrDefault(r => r.Id == id) ?? throw new Application.Admin.Common.NotFoundException("The role was not found.");
    }
}
