using System.ComponentModel.DataAnnotations;
using AppInHouse.Identity.Application.Admin.Common;

namespace AppInHouse.Identity.Application.Admin.Roles;

public sealed record RolePermissionDto(Guid PermissionId, string PermissionCode);

/// <param name="Permissions">Only populated for global administrators; null otherwise.</param>
public sealed record RoleDto(
    string Id,
    string Name,
    string? Description,
    DateTime CreatedAt,
    IReadOnlyList<RolePermissionDto>? Permissions);

public class RoleListQuery : PagingQuery
{
    [StringLength(100)]
    public string? Search { get; set; }
}

public sealed class CreateRoleRequest
{
    [Required, StringLength(256)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }
}

public sealed class UpdateRoleRequest
{
    [Required, StringLength(256)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }
}

public sealed class SetRolePermissionsRequest
{
    /// <summary>The complete set of permissions of the role (an empty list removes them all).</summary>
    [Required]
    public List<Guid> PermissionIds { get; set; } = new();
}

public interface IRoleAdminService
{
    Task<PagedResult<RoleDto>> ListAsync(RoleListQuery query, CancellationToken cancellationToken = default);

    Task<RoleDto> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default);

    Task<RoleDto> UpdateAsync(string id, UpdateRoleRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the permissions of the role and invalidates every cached permission set
    /// (a role can be held by any number of users in any number of projects).
    /// </summary>
    Task<RoleDto> SetPermissionsAsync(string id, SetRolePermissionsRequest request, CancellationToken cancellationToken = default);
}
