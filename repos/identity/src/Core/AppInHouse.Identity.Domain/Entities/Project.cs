using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>A client project/tenant (e.g. ERP, AIChatBot, MobileApp) that roles and permissions are scoped to.</summary>
public class Project
{
    public const int ProjectCodeMaxLength = 50;
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int ConcurrencyTokenMaxLength = 50;

    // Required by EF Core.
    private Project() { }

    public Project(string projectCode, string name, string? description = null)
    {
        Id = Guid.NewGuid();
        ProjectCode = Guard.Required(projectCode, ProjectCodeMaxLength, nameof(projectCode));
        Name = Guard.Required(name, NameMaxLength, nameof(name));
        Description = Guard.Optional(description, DescriptionMaxLength, nameof(description));
        IsActive = true;
        IsDeleted = false;
        CreatedAt = DateTime.UtcNow;
        ConcurrencyToken = NewToken();
    }

    public Guid Id { get; private set; }
    public string ProjectCode { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public string ConcurrencyToken { get; private set; } = string.Empty;

    public void UpdateDetails(string name, string? description)
    {
        Name = Guard.Required(name, NameMaxLength, nameof(name));
        Description = Guard.Optional(description, DescriptionMaxLength, nameof(description));
        RotateConcurrencyToken();
    }

    public void Activate()
    {
        if (IsDeleted)
            throw new InvalidOperationException("A deleted project cannot be activated.");

        IsActive = true;
        RotateConcurrencyToken();
    }

    public void Deactivate()
    {
        IsActive = false;
        RotateConcurrencyToken();
    }

    /// <summary>Soft delete: the project is kept for audit purposes but is no longer active.</summary>
    public void MarkDeleted()
    {
        IsDeleted = true;
        IsActive = false;
        RotateConcurrencyToken();
    }

    private void RotateConcurrencyToken() => ConcurrencyToken = NewToken();

    private static string NewToken() => Guid.NewGuid().ToString("N");
}
