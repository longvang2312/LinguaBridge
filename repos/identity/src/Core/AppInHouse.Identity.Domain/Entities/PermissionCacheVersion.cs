using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>
/// Version counter per context key (e.g. <c>User:{id}</c>, <c>Project:{code}</c>); bumped on every permission
/// change so cached permission sets can be detected as stale.
/// </summary>
public class PermissionCacheVersion
{
    public const int ContextKeyMaxLength = 150;

    // Required by EF Core.
    private PermissionCacheVersion() { }

    public PermissionCacheVersion(string contextKey)
    {
        ContextKey = Guard.Required(contextKey, ContextKeyMaxLength, nameof(contextKey));
        VersionNumber = 0;
        LastInvalidatedAt = DateTime.UtcNow;
    }

    public string ContextKey { get; private set; } = string.Empty;
    public long VersionNumber { get; private set; }
    public DateTime LastInvalidatedAt { get; private set; }

    public void Invalidate()
    {
        VersionNumber++;
        LastInvalidatedAt = DateTime.UtcNow;
    }
}
