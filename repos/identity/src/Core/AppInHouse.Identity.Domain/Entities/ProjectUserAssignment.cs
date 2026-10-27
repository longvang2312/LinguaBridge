using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>Membership of a user (AspNetUsers) in a <see cref="Project"/>.</summary>
public class ProjectUserAssignment
{
    public const int UserIdMaxLength = 450;

    // Required by EF Core.
    private ProjectUserAssignment() { }

    public ProjectUserAssignment(Guid projectId, string userId, bool isProjectAdmin = false)
    {
        Id = Guid.NewGuid();
        ProjectId = Guard.RequiredId(projectId, nameof(projectId));
        UserId = Guard.Required(userId, UserIdMaxLength, nameof(userId));
        IsProjectAdmin = isProjectAdmin;
        IsBlocked = false;
        AssignedAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public bool IsProjectAdmin { get; private set; }
    public bool IsBlocked { get; private set; }
    public DateTime AssignedAt { get; private set; }

    public Project Project { get; private set; } = null!;

    public void SetProjectAdmin(bool isProjectAdmin) => IsProjectAdmin = isProjectAdmin;

    public void Block() => IsBlocked = true;

    public void Unblock() => IsBlocked = false;
}
