using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Abstractions;

/// <summary>
/// Protects the one secret of a configuration (SMTP password, bot token, S3 secret key) before it is stored.
/// What comes out of <see cref="Protect"/> is safe to keep in the database; only a host that holds the key ring can read it back.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    /// <exception cref="SecretProtectionException">The text was not protected by this key ring, or was altered.</exception>
    string Unprotect(string protectedText);
}

public sealed class SecretProtectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Who is doing this: a user's email, "apikey:aih_live_xxxxxx", or "system".</summary>
public interface ICurrentActor
{
    string Name { get; }
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IChannelConfigRepository
{
    /// <summary>Every version of one series, tracked so a change to them is saved by the unit of work.</summary>
    Task<IReadOnlyList<ChannelConfiguration>> GetSeriesAsync(
        string projectCode, ChannelType channelType, string name, CancellationToken cancellationToken = default);

    Task<ChannelConfiguration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>For reading only (not tracked). Null filters mean "all".</summary>
    Task<IReadOnlyList<ChannelConfiguration>> ListAsync(
        string? projectCode, ChannelType? channelType, ConfigurationStatus? status, CancellationToken cancellationToken = default);

    Task AddAsync(ChannelConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface IApiKeyRepository
{
    /// <summary>The keys whose prefix matches (more than one while an old key is still in its grace period).</summary>
    Task<IReadOnlyList<ApiKey>> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);

    Task AddAsync(ApiKey key, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Adds an audit entry to the current unit of work, so it is saved together with the change it describes (both or neither).
/// <paramref name="details"/> must never contain a secret.
/// </summary>
public interface IAuditLogWriter
{
    void Record(string action, string resourceType, string resourceId, object? details = null);
}
