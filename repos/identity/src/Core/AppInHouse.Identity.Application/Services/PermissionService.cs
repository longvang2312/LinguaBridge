using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Authorization;
using Microsoft.Extensions.Logging;

namespace AppInHouse.Identity.Application.Services;

public class PermissionService : IPermissionService
{
    private readonly IPermissionDataSource _dataSource;
    private readonly IPermissionVersionStore _versionStore;
    private readonly IPermissionCacheService _cache;
    private readonly ILogger<PermissionService> _logger;
    private readonly TimeProvider _timeProvider;

    public PermissionService(
        IPermissionDataSource dataSource,
        IPermissionVersionStore versionStore,
        IPermissionCacheService cache,
        ILogger<PermissionService> logger,
        TimeProvider? timeProvider = null)
    {
        _dataSource = dataSource;
        _versionStore = versionStore;
        _cache = cache;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        string userId, string projectCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(projectCode))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The version is read BEFORE the permissions are computed: if a change lands in between, the entry is stored
        // with the older version and is recomputed on the next call instead of being served stale.
        var currentVersion = await _versionStore.GetCombinedVersionAsync(
            PermissionCacheKeys.VersionContexts(userId, projectCode), cancellationToken);

        var cached = await _cache.GetAsync(userId, projectCode, cancellationToken);
        if (cached is not null && cached.Version == currentVersion)
            return cached.Permissions;

        var permissions = await ComputeAsync(userId, projectCode, cancellationToken);
        await _cache.SetAsync(userId, projectCode, new CachedPermissionsModel(currentVersion, permissions), cancellationToken);

        return permissions;
    }

    public async Task<bool> HasPermissionAsync(
        string userId, string projectCode, string permission, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(projectCode) || string.IsNullOrWhiteSpace(permission))
            return false;

        try
        {
            var permissions = await GetEffectivePermissionsAsync(userId, projectCode, cancellationToken);
            return permissions.Contains(permission.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-safe: when the cache or the data source is broken, access is denied rather than granted.
            _logger.LogError(ex, "Permission check failed for user {UserId} in project {ProjectCode}; denying access.", userId, projectCode);
            return false;
        }
    }

    /// <summary>
    /// effective = (role permissions + direct grants) - direct revocations, or everything for project / global admins.
    /// </summary>
    private async Task<HashSet<string>> ComputeAsync(string userId, string projectCode, CancellationToken cancellationToken)
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var user = await _dataSource.GetUserAccessStateAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
            return empty;

        if (user.LockoutEnd is { } lockoutEnd && lockoutEnd > _timeProvider.GetUtcNow())
            return empty;

        var project = await _dataSource.FindProjectAsync(projectCode, cancellationToken);
        if (project is null || !project.IsActive || project.IsDeleted)
            return empty;

        var membership = await _dataSource.GetProjectMembershipAsync(project.Id, userId, cancellationToken);
        if (membership is { IsBlocked: true })
            return empty;

        // Admins get everything. An explicit project block (above) still wins over GlobalAdmin.
        if (membership is { IsProjectAdmin: true } || await _dataSource.IsGlobalAdminAsync(userId, cancellationToken))
        {
            return new HashSet<string>(
                await _dataSource.GetAllPermissionCodesAsync(project.Id, cancellationToken),
                StringComparer.OrdinalIgnoreCase);
        }

        var permissions = new HashSet<string>(
            await _dataSource.GetRolePermissionCodesAsync(userId, project.Id, cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        var direct = await _dataSource.GetDirectPermissionsAsync(userId, project.Id, cancellationToken);
        permissions.UnionWith(direct.Where(d => !d.IsRevoked).Select(d => d.PermissionCode));
        permissions.ExceptWith(direct.Where(d => d.IsRevoked).Select(d => d.PermissionCode));

        return permissions;
    }
}
