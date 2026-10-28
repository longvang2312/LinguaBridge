using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>A permission code (e.g. <c>users.view</c>). Global when <see cref="ProjectId"/> is null.</summary>
public class Permission
{
    public const int PermissionCodeMaxLength = 100;
    public const int DisplayNameMaxLength = 150;
    public const int ModuleMaxLength = 50;

    // Required by EF Core.
    private Permission() { }

    /// <param name="projectId">Owning project, or null for a global permission.</param>
    public Permission(string permissionCode, string displayName, string module, Guid? projectId = null)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("projectId must not be an empty Guid.", nameof(projectId));

        Id = Guid.NewGuid();
        PermissionCode = Guard.Required(permissionCode, PermissionCodeMaxLength, nameof(permissionCode));
        DisplayName = Guard.Required(displayName, DisplayNameMaxLength, nameof(displayName));
        Module = Guard.Required(module, ModuleMaxLength, nameof(module));
        ProjectId = projectId;
        IsGlobal = projectId is null;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string PermissionCode { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string Module { get; private set; } = string.Empty;
    public bool IsGlobal { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public Project? Project { get; private set; }

    /// <summary>Changes the descriptive fields; the code and the owning project are immutable.</summary>
    public void UpdateDetails(string displayName, string module)
    {
        var newDisplayName = Guard.Required(displayName, DisplayNameMaxLength, nameof(displayName));
        var newModule = Guard.Required(module, ModuleMaxLength, nameof(module));

        DisplayName = newDisplayName;
        Module = newModule;
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;
}
