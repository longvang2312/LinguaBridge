namespace AppInHouse.Identity.Infrastructure.Caching;

public class PermissionCacheOptions
{
    /// <summary>Upper bound of how long an entry lives; version checks and invalidation usually evict it sooner.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);
}
