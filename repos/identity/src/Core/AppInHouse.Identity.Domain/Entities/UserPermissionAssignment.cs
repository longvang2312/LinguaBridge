using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>
/// Direct permission grant to a user within a project, or (when <see cref="IsRevoked"/>) an explicit
/// revocation that overrides the permission even if one of the user's roles grants it.
/// </summary>
public class UserPermissionAssignment
{
    public const int UserIdMaxLength = 450;

    // Required by EF Core.
    private UserPermissionAssignment() { }

    public UserPermissionAssignment(string userId, Guid permissionId, Guid projectId, bool isRevoked = false)
    {
        Id = Guid.NewGuid();
        UserId = Guard.Required(userId, UserIdMaxLength, nameof(userId));
        PermissionId = Guard.RequiredId(permissionId, nameof(permissionId));
        ProjectId = Guard.RequiredId(projectId, nameof(projectId));
        IsRevoked = isRevoked;
        AssignedAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public Guid PermissionId { get; private set; }
    public Guid ProjectId { get; private set; }
    public bool IsRevoked { get; private set; }
    public DateTime AssignedAt { get; private set; }

    public Permission Permission { get; private set; } = null!;
    public Project Project { get; private set; } = null!;
}
