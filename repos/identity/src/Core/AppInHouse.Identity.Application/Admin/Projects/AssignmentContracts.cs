using System.ComponentModel.DataAnnotations;

namespace AppInHouse.Identity.Application.Admin.Projects;

public sealed class AssignProjectUserRequest
{
    [Required, StringLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Only a global administrator may grant or change this flag.</summary>
    public bool IsProjectAdmin { get; set; }
}

public sealed record ProjectUserDto(Guid ProjectId, string UserId, bool IsProjectAdmin, bool IsBlocked, DateTime AssignedAt);

public sealed class SetUserRolesRequest
{
    /// <summary>The complete set of roles the user holds in the project (an empty list removes them all).</summary>
    [Required]
    public List<string> RoleIds { get; set; } = new();
}

public sealed record UserRolesDto(Guid ProjectId, string UserId, IReadOnlyList<string> RoleIds);

public sealed class DirectPermissionItem
{
    [Required]
    public Guid? PermissionId { get; set; }

    /// <summary>True blocks the permission even if one of the user's roles grants it.</summary>
    public bool IsRevoked { get; set; }
}

public sealed class SetUserPermissionsRequest
{
    /// <summary>The complete set of direct grants / revocations of the user in the project.</summary>
    [Required]
    public List<DirectPermissionItem> Assignments { get; set; } = new();
}

public sealed record DirectPermissionDto(Guid PermissionId, string PermissionCode, bool IsRevoked);

public sealed record UserPermissionsDto(Guid ProjectId, string UserId, IReadOnlyList<DirectPermissionDto> Assignments);

/// <summary>A member of a project with the roles and direct permission entries held there.</summary>
public sealed record ProjectMemberDto(
    string UserId,
    string Email,
    string FullName,
    bool IsProjectAdmin,
    bool IsBlocked,
    DateTime AssignedAt,
    IReadOnlyList<string> RoleIds,
    IReadOnlyList<DirectPermissionDto> DirectPermissions);

/// <summary>A project a user belongs to.</summary>
public sealed record UserProjectDto(Guid ProjectId, string ProjectCode, string ProjectName, bool IsProjectAdmin, bool IsBlocked);

public interface IProjectAssignmentService
{
    /// <summary>The members of the project (project administrators of that project and global administrators only).</summary>
    Task<IReadOnlyList<ProjectMemberDto>> ListMembersAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The projects the user belongs to. Global administrators see all of them; project administrators only the
    /// ones they administer themselves.
    /// </summary>
    Task<IReadOnlyList<UserProjectDto>> ListUserProjectsAsync(string userId, CancellationToken cancellationToken = default);

    Task<ProjectUserDto> AssignUserAsync(Guid projectId, AssignProjectUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the user's roles in the project, then invalidates the user's cached permissions.</summary>
    Task<UserRolesDto> SetUserRolesAsync(Guid projectId, string userId, SetUserRolesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the user's direct grants/revocations in the project, then invalidates the user's cached permissions.</summary>
    Task<UserPermissionsDto> SetUserPermissionsAsync(Guid projectId, string userId, SetUserPermissionsRequest request, CancellationToken cancellationToken = default);
}
