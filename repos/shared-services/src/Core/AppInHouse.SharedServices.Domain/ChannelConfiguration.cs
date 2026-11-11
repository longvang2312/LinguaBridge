namespace AppInHouse.SharedServices.Domain;

/// <summary>
/// One version of the settings of a channel (for example the SMTP server of project "linguabridge").
/// A series is all versions that share <see cref="ProjectCode"/>, <see cref="ChannelType"/> and <see cref="Name"/>; at most one
/// version of a series is Active. Non-sensitive values live in <see cref="SettingsJson"/>; the password, token or key is only
/// ever kept protected in <see cref="EncryptedSecret"/>. Versions are changed through <see cref="ChannelConfigurationVersioning"/>.
/// </summary>
public sealed class ChannelConfiguration
{
    // For EF Core.
    private ChannelConfiguration()
    {
        ProjectCode = string.Empty;
        Name = string.Empty;
        SettingsJson = "{}";
    }

    public Guid Id { get; private set; }
    public string ProjectCode { get; private set; }
    public ChannelType ChannelType { get; private set; }
    public ProviderType ProviderType { get; private set; }
    public string Name { get; private set; }

    /// <summary>1, 2, 3, ... within the series; never reused.</summary>
    public int Version { get; private set; }

    public ConfigurationStatus Status { get; private set; }

    /// <summary>Host, port, bucket, region and the like. Never a password or token.</summary>
    public string SettingsJson { get; private set; }

    /// <summary>The secret, already protected (see ISecretProtector). Null when the provider needs none.</summary>
    public string? EncryptedSecret { get; private set; }

    public DateTimeOffset? LastTestedAt { get; private set; }
    public TestStatus LastTestStatus { get; private set; }

    /// <summary>When this version last became Active; what a rollback uses to find "the previous active version".</summary>
    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool HasSecret => !string.IsNullOrEmpty(EncryptedSecret);

    /// <summary>The first version of a new series: a Draft, version 1.</summary>
    public static ChannelConfiguration CreateFirstVersion(
        string projectCode, ChannelType channelType, ProviderType providerType, string name,
        string settingsJson, string? encryptedSecret, DateTimeOffset now) =>
        Create(projectCode, channelType, providerType, name, version: 1, settingsJson, encryptedSecret, now);

    internal static ChannelConfiguration Create(
        string projectCode, ChannelType channelType, ProviderType providerType, string name,
        int version, string settingsJson, string? encryptedSecret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(projectCode)) throw new DomainException("A configuration needs a project code.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A configuration needs a name.");
        if (version < 1) throw new DomainException("A version number starts at 1.");
        if (!ProviderCatalog.Supports(channelType, providerType))
            throw new DomainException($"The {providerType} provider cannot serve the {channelType} channel.");

        return new ChannelConfiguration
        {
            Id = Guid.NewGuid(),
            ProjectCode = projectCode,
            ChannelType = channelType,
            ProviderType = providerType,
            Name = name,
            Version = version,
            Status = ConfigurationStatus.Draft,
            SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson,
            EncryptedSecret = string.IsNullOrEmpty(encryptedSecret) ? null : encryptedSecret,
            LastTestStatus = TestStatus.Untested,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Records the result of a "send test"; a test never activates or changes the stored settings.</summary>
    public void RecordTest(TestStatus result, DateTimeOffset now)
    {
        LastTestStatus = result;
        LastTestedAt = now;
        UpdatedAt = now;
    }

    internal void MarkActive(DateTimeOffset now)
    {
        Status = ConfigurationStatus.Active;
        ActivatedAt = now;
        UpdatedAt = now;
    }

    internal void MarkArchived(DateTimeOffset now)
    {
        Status = ConfigurationStatus.Archived;
        UpdatedAt = now;
    }
}
