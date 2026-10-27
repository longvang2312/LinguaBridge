using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.Infrastructure.Caching;

/// <summary>
/// <see cref="IMemoryCache"/>-backed permission cache. <see cref="IMemoryCache"/> cannot enumerate its keys, so the
/// service keeps a small index (user -> entries, project -> entries) to support evicting by user or project.
/// A distributed implementation (Redis) only has to implement <see cref="IPermissionCacheService"/>.
/// </summary>
public sealed class PermissionCacheService : IPermissionCacheService
{
    private readonly IMemoryCache _cache;
    private readonly PermissionCacheOptions _options;

    private readonly object _gate = new();
    private readonly Dictionary<string, (string UserKey, string ProjectKey)> _entries = new();
    private readonly Dictionary<string, HashSet<string>> _byUser = new();
    private readonly Dictionary<string, HashSet<string>> _byProject = new();

    public PermissionCacheService(IMemoryCache cache, IOptions<PermissionCacheOptions> options)
    {
        _cache = cache;
        _options = options.Value;
    }

    public Task<CachedPermissionsModel?> GetAsync(string userId, string projectCode, CancellationToken cancellationToken = default)
    {
        var found = _cache.TryGetValue(PermissionCacheKeys.Entry(userId, projectCode), out CachedPermissionsModel? model);
        return Task.FromResult(found ? model : null);
    }

    public Task SetAsync(string userId, string projectCode, CachedPermissionsModel model, CancellationToken cancellationToken = default)
    {
        var key = PermissionCacheKeys.Entry(userId, projectCode);
        var userKey = PermissionCacheKeys.UserContext(userId);
        var projectKey = PermissionCacheKeys.ProjectContext(projectCode);

        lock (_gate)
        {
            _entries[key] = (userKey, projectKey);
            Index(_byUser, userKey).Add(key);
            Index(_byProject, projectKey).Add(key);
        }

        var entryOptions = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.Ttl };
        entryOptions.RegisterPostEvictionCallback(OnEvicted, key);
        _cache.Set(key, model, entryOptions);

        return Task.CompletedTask;
    }

    public Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default)
        => RemoveIndexed(_byUser, PermissionCacheKeys.UserContext(userId));

    public Task RemoveProjectAsync(string projectCode, CancellationToken cancellationToken = default)
        => RemoveIndexed(_byProject, PermissionCacheKeys.ProjectContext(projectCode));

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        string[] keys;
        lock (_gate)
            keys = _entries.Keys.ToArray();

        foreach (var key in keys)
            _cache.Remove(key);

        return Task.CompletedTask;
    }

    private Task RemoveIndexed(Dictionary<string, HashSet<string>> index, string indexKey)
    {
        string[] keys;
        lock (_gate)
            keys = index.TryGetValue(indexKey, out var set) ? set.ToArray() : Array.Empty<string>();

        foreach (var key in keys)
            _cache.Remove(key);

        return Task.CompletedTask;
    }

    private static HashSet<string> Index(Dictionary<string, HashSet<string>> index, string key)
    {
        if (!index.TryGetValue(key, out var set))
            index[key] = set = new HashSet<string>();

        return set;
    }

    /// <summary>Keeps the index in sync once an entry is gone (expired, removed or capacity-evicted).</summary>
    private void OnEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        // A replaced entry is immediately re-indexed by SetAsync.
        if (reason == EvictionReason.Replaced)
            return;

        var entryKey = (string)key;

        // The key may have been re-added since the old entry was evicted; its index record must stay then.
        if (_cache.TryGetValue(entryKey, out _))
            return;

        lock (_gate)
        {
            if (!_entries.Remove(entryKey, out var owners))
                return;

            Unindex(_byUser, owners.UserKey, entryKey);
            Unindex(_byProject, owners.ProjectKey, entryKey);
        }
    }

    private static void Unindex(Dictionary<string, HashSet<string>> index, string indexKey, string entryKey)
    {
        if (!index.TryGetValue(indexKey, out var set))
            return;

        set.Remove(entryKey);
        if (set.Count == 0)
            index.Remove(indexKey);
    }
}
