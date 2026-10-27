using AppInHouse.Identity.Application.Authorization;

namespace AppInHouse.Identity.Application.Abstractions;

/// <summary>
/// Cache of effective permissions. Async so a distributed implementation (e.g. Redis) can replace the
/// in-memory one. Version validation is the caller's job: this service only stores and removes entries.
/// </summary>
public interface IPermissionCacheService
{
    Task<CachedPermissionsModel?> GetAsync(string userId, string projectCode, CancellationToken cancellationToken = default);

    Task SetAsync(string userId, string projectCode, CachedPermissionsModel model, CancellationToken cancellationToken = default);

    Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default);

    Task RemoveProjectAsync(string projectCode, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
