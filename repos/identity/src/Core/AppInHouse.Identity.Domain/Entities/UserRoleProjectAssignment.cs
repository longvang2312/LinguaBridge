using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>Assigns a role (AspNetRoles) to a user (AspNetUsers) within a specific <see cref="Project"/>.</summary>
public class UserRoleProjectAssignment
{
    public const int UserIdMaxLength = 450;
    public const int RoleIdMaxLength = 450;

    // Required by EF Core.
    private UserRoleProjectAssignment() { }

    public UserRoleProjectAssignment(string userId, string roleId, Guid projectId)
    {
        Id = Guid.NewGuid();
        UserId = Guard.Required(userId, UserIdMaxLength, nameof(userId));
        RoleId = Guard.Required(roleId, RoleIdMaxLength, nameof(roleId));
        ProjectId = Guard.RequiredId(projectId, nameof(projectId));
        AssignedAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public string RoleId { get; private set; } = string.Empty;
    public Guid ProjectId { get; private set; }
    public DateTime AssignedAt { get; private set; }

    public Project Project { get; private set; } = null!;
}
