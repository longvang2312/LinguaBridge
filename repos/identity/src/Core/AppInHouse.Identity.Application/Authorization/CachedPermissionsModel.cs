namespace AppInHouse.Identity.Application.Authorization;

/// <summary>Effective permissions of a (user, project) together with the version they were computed at.</summary>
/// <param name="Version">Combined version of every context that influences the entry (see <see cref="PermissionCacheKeys.VersionContexts"/>).</param>
/// <param name="Permissions">Treated as read-only: the instance is shared between requests.</param>
public sealed record CachedPermissionsModel(long Version, HashSet<string> Permissions);
