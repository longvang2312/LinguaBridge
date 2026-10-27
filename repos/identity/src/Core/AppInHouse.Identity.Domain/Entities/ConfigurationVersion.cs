using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>Records an applied configuration version.</summary>
public class ConfigurationVersion
{
    public const int DescriptionMaxLength = 255;

    // Required by EF Core.
    private ConfigurationVersion() { }

    public ConfigurationVersion(int versionNumber, string description)
    {
        if (versionNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(versionNumber), "versionNumber must be greater than zero.");

        Id = Guid.NewGuid();
        VersionNumber = versionNumber;
        Description = Guard.Required(description, DescriptionMaxLength, nameof(description));
        AppliedAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public int VersionNumber { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public DateTime AppliedAt { get; private set; }
}
