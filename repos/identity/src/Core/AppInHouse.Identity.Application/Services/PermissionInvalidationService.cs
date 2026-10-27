using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Authorization;
using Microsoft.Extensions.Logging;

namespace AppInHouse.Identity.Application.Services;

public class PermissionInvalidationService : IPermissionInvalidationService
{
    private readonly IPermissionVersionStore _versionStore;
    private readonly IPermissionCacheService _cache;
    private readonly ILogger<PermissionInvalidationService> _logger;

    public PermissionInvalidationService(
        IPermissionVersionStore versionStore,
        IPermissionCacheService cache,
        ILogger<PermissionInvalidationService> logger)
    {
        _versionStore = versionStore;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvalidateUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        await _versionStore.IncrementAsync(PermissionCacheKeys.UserContext(userId), cancellationToken);
        await EvictAsync(() => _cache.RemoveUserAsync(userId, cancellationToken), $"user {userId}");
    }

    public async Task InvalidateProjectAsync(string projectCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectCode);

        await _versionStore.IncrementAsync(PermissionCacheKeys.ProjectContext(projectCode), cancellationToken);
        await EvictAsync(() => _cache.RemoveProjectAsync(projectCode, cancellationToken), $"project {projectCode}");
    }

    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        await _versionStore.IncrementAsync(PermissionCacheKeys.GlobalContext, cancellationToken);
        await EvictAsync(() => _cache.ClearAsync(cancellationToken), "all entries");
    }

    /// <summary>
    /// The version bump is what guarantees correctness (every node re-validates against it). Evicting the local entries
    /// is an optimisation, so a failure here is logged but must not fail the admin operation.
    /// </summary>
    private async Task EvictAsync(Func<Task> evict, string scope)
    {
        try
        {
            await evict();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Permission cache eviction failed for {Scope}; the version bump still invalidates it.", scope);
        }
    }
}
