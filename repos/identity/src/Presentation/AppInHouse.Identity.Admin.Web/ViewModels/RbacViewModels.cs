using AppInHouse.Identity.Application.Admin.Permissions;
using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Roles;

namespace AppInHouse.Identity.Admin.Web.ViewModels;

public sealed record RoleListViewModel(RoleListQuery Query, IReadOnlyList<RoleDto> Items, PagerModel Pager, bool IsGlobalAdmin);

/// <summary>Permissions (rows, grouped by module) against roles (columns).</summary>
public sealed record RoleMatrixViewModel(IReadOnlyList<RoleDto> Roles, IReadOnlyList<PermissionDto> Permissions)
{
    public IEnumerable<IGrouping<string, PermissionDto>> Modules
        => Permissions.GroupBy(p => p.Module).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    public bool Holds(RoleDto role, PermissionDto permission)
        => role.Permissions?.Any(p => p.PermissionId == permission.Id) == true;
}

public sealed record PermissionListViewModel(
    PermissionListQuery Query,
    IReadOnlyList<PermissionDto> Items,
    PagerModel Pager,
    IReadOnlyList<ProjectDto> Projects,
    bool IsGlobalAdmin);

public sealed class PermissionFormViewModel
{
    public Guid? Id { get; set; }

    public bool IsNew => Id is null;

    public CreatePermissionRequest Form { get; set; } = new();

    public bool IsActive { get; set; } = true;

    /// <summary>The projects a permission can be created for (all for global administrators, their own otherwise).</summary>
    public IReadOnlyList<ProjectDto> Projects { get; set; } = Array.Empty<ProjectDto>();

    public bool IsGlobalAdmin { get; set; }
}
