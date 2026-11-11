namespace AppInHouse.SharedServices.Domain;

/// <summary>
/// The version rules of one series of <see cref="ChannelConfiguration"/>s (same project, channel and name).
/// <list type="bullet">
/// <item>Saving creates the next version as a Draft; settings of a saved version never change.</item>
/// <item>Activating a version makes it Active and archives the Active one and every older Draft. Newer Drafts stay.</item>
/// <item>Rolling back re-activates an archived version (the most recently active one, or a chosen one) and archives the Active one;
/// Drafts are left alone, so work in progress is not lost.</item>
/// </list>
/// All methods work on the versions the caller loaded; saving them is the caller's job.
/// </summary>
public static class ChannelConfigurationVersioning
{
    public static int NextVersion(IReadOnlyCollection<ChannelConfiguration> series) =>
        series.Count == 0 ? 1 : series.Max(v => v.Version) + 1;

    public static ChannelConfiguration? ActiveVersion(IReadOnlyCollection<ChannelConfiguration> series) =>
        series.SingleOrDefault(v => v.Status == ConfigurationStatus.Active);

    /// <summary>
    /// Creates the next Draft of a series. A null <paramref name="encryptedSecret"/> keeps the secret of the latest version, so the
    /// settings can change without typing the password again, but only while the provider stays the same: the secret of one provider
    /// (an SMTP password) must never become the credential of another (a SendGrid API key).
    /// </summary>
    public static ChannelConfiguration CreateNextVersion(
        IReadOnlyCollection<ChannelConfiguration> series, ProviderType providerType,
        string settingsJson, string? encryptedSecret, DateTimeOffset now)
    {
        if (series.Count == 0) throw new DomainException("There is no version to build on; create the first version instead.");

        var latest = series.OrderBy(v => v.Version).Last();
        var inherited = latest.ProviderType == providerType ? latest.EncryptedSecret : null;
        return ChannelConfiguration.Create(
            latest.ProjectCode, latest.ChannelType, providerType, latest.Name, NextVersion(series),
            settingsJson, encryptedSecret ?? inherited, now);
    }

    public static ChannelConfiguration Activate(IReadOnlyCollection<ChannelConfiguration> series, int version, DateTimeOffset now)
    {
        var target = Find(series, version);
        if (target.Status == ConfigurationStatus.Active)
            throw new DomainException($"Version {version} is already active.");

        foreach (var other in series.Where(v => v.Status == ConfigurationStatus.Active
                                                || (v.Status == ConfigurationStatus.Draft && v.Version < version)))
            other.MarkArchived(now);

        target.MarkActive(now);
        return target;
    }

    /// <summary>Back to <paramref name="targetVersion"/> or, when null, to the version that was active before the current one.</summary>
    public static ChannelConfiguration Rollback(IReadOnlyCollection<ChannelConfiguration> series, int? targetVersion, DateTimeOffset now)
    {
        var current = ActiveVersion(series)
            ?? throw new DomainException("There is no active version to roll back from.");

        ChannelConfiguration target;
        if (targetVersion is { } wanted)
        {
            target = Find(series, wanted);
            if (target.Status == ConfigurationStatus.Active) throw new DomainException($"Version {wanted} is already active.");
            if (target.Status == ConfigurationStatus.Draft)
                throw new DomainException($"Version {wanted} is a draft, not an earlier version; activate it instead.");
        }
        else
        {
            target = series
                .Where(v => v.Status == ConfigurationStatus.Archived && v.ActivatedAt is not null)
                .OrderByDescending(v => v.ActivatedAt)
                .ThenByDescending(v => v.Version)
                .FirstOrDefault()
                ?? throw new DomainException("No earlier version has been active, so there is nothing to roll back to.");
        }

        current.MarkArchived(now);
        target.MarkActive(now);
        return target;
    }

    private static ChannelConfiguration Find(IReadOnlyCollection<ChannelConfiguration> series, int version) =>
        series.SingleOrDefault(v => v.Version == version)
        ?? throw new DomainException($"Version {version} does not exist.");
}
