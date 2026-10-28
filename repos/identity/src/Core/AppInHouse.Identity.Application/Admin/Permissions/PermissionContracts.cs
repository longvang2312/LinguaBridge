using System.ComponentModel.DataAnnotations;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Domain.Entities;

namespace AppInHouse.Identity.Application.Admin.Permissions;

public sealed record PermissionDto(
    Guid Id,
    Guid? ProjectId,
    string PermissionCode,
    string DisplayName,
    string Module,
    bool IsGlobal,
    bool IsActive,
    DateTime CreatedAt);

public class PermissionListQuery : PagingQuery
{
    public Guid? ProjectId { get; set; }

    [StringLength(Permission.ModuleMaxLength)]
    public string? Module { get; set; }

    [StringLength(100)]
    public string? Search { get; set; }

    public bool? IsActive { get; set; }
}

public sealed class CreatePermissionRequest
{
    [Required, StringLength(Permission.PermissionCodeMaxLength), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$")]
    public string PermissionCode { get; set; } = string.Empty;

    [Required, StringLength(Permission.DisplayNameMaxLength)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, StringLength(Permission.ModuleMaxLength)]
    public string Module { get; set; } = string.Empty;

    /// <summary>The owning project; omitted creates a global permission (global administrators only).</summary>
    public Guid? ProjectId { get; set; }
}

/// <summary>The code and the project of a permission are immutable.</summary>
public sealed class UpdatePermissionRequest
{
    [Required, StringLength(Permission.DisplayNameMaxLength)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, StringLength(Permission.ModuleMaxLength)]
    public string Module { get; set; } = string.Empty;

    [Required]
    public bool? IsActive { get; set; }
}

public interface IPermissionAdminService
{
    Task<PagedResult<PermissionDto>> ListAsync(PermissionListQuery query, CancellationToken cancellationToken = default);

    Task<PermissionDto> CreateAsync(CreatePermissionRequest request, CancellationToken cancellationToken = default);

    Task<PermissionDto> UpdateAsync(Guid id, UpdatePermissionRequest request, CancellationToken cancellationToken = default);
}
