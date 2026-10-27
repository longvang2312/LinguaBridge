namespace AppInHouse.Identity.Application.Abstractions;

/// <summary>The durable version counters (<c>PermissionCacheVersions</c>) shared by every node.</summary>
public interface IPermissionVersionStore
{
    /// <summary>Sum of the versions of the given context keys; keys that were never bumped count as 0.</summary>
    Task<long> GetCombinedVersionAsync(IReadOnlyCollection<string> contextKeys, CancellationToken cancellationToken = default);

    /// <summary>Atomically increments the version of a context key, creating it when missing.</summary>
    Task IncrementAsync(string contextKey, CancellationToken cancellationToken = default);
}
