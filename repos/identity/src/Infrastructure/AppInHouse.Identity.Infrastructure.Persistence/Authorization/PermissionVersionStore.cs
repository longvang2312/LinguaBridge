using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Authorization;

/// <summary>
/// <see cref="IPermissionVersionStore"/> over the <c>PermissionCacheVersions</c> table.
/// </summary>
public class PermissionVersionStore : IPermissionVersionStore
{
    private const int MaxIncrementAttempts = 5;

    private readonly AppAuthDbContext _context;

    public PermissionVersionStore(AppAuthDbContext context) => _context = context;

    public async Task<long> GetCombinedVersionAsync(IReadOnlyCollection<string> contextKeys, CancellationToken cancellationToken = default)
    {
        var keys = contextKeys.ToArray();

        // Always read from the database (no tracking, no caching): this is what makes revocation visible to every node.
        var versions = await _context.PermissionCacheVersions.AsNoTracking()
            .Where(v => keys.Contains(v.ContextKey))
            .Select(v => v.VersionNumber)
            .ToListAsync(cancellationToken);

        return versions.Sum();
    }

    public Task IncrementAsync(string contextKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextKey);

        return _context.Database.IsRelational()
            ? IncrementAtomicallyAsync(contextKey, cancellationToken)
            : IncrementInMemoryAsync(contextKey, cancellationToken);
    }

    /// <summary>
    /// A single UPDATE ... SET VersionNumber = VersionNumber + 1, so concurrent admins never lose an increment (a lost
    /// increment could let a reader cache stale permissions under an unchanged version). When the row does not exist
    /// yet it is inserted; a concurrent insert of the same key is retried as an update.
    /// </summary>
    private async Task IncrementAtomicallyAsync(string contextKey, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxIncrementAttempts; attempt++)
        {
            var now = DateTime.UtcNow;
            var updated = await _context.PermissionCacheVersions
                .Where(v => v.ContextKey == contextKey)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(v => v.VersionNumber, v => v.VersionNumber + 1)
                          .SetProperty(v => v.LastInvalidatedAt, now),
                    cancellationToken);

            if (updated > 0)
                return;

            try
            {
                var created = new PermissionCacheVersion(contextKey);
                created.Invalidate();
                _context.PermissionCacheVersions.Add(created);
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException)
            {
                // Another writer created the row first: forget our insert and increment its row instead.
                _context.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException($"Could not increment the permission cache version of '{contextKey}'.");
    }

    /// <summary>
    /// Fallback for providers without set-based updates (EF Core InMemory, used by tests). Not atomic.
    /// </summary>
    private async Task IncrementInMemoryAsync(string contextKey, CancellationToken cancellationToken)
    {
        var version = await _context.PermissionCacheVersions.FirstOrDefaultAsync(v => v.ContextKey == contextKey, cancellationToken);
        if (version is null)
        {
            version = new PermissionCacheVersion(contextKey);
            _context.PermissionCacheVersions.Add(version);
        }

        version.Invalidate();
        await _context.SaveChangesAsync(cancellationToken);
    }
}
