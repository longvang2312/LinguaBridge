using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Authorization;

namespace AppInHouse.Identity.UnitTests.Support;

/// <summary>In-memory, configurable <see cref="IPermissionDataSource"/> that also counts calls.</summary>
public class FakeDataSource : IPermissionDataSource
{
    public static readonly Guid ProjectId = Guid.NewGuid();

    public UserAccessState? User { get; set; } = new(IsActive: true, LockoutEnd: null);
    public bool GlobalAdmin { get; set; }
    public ProjectAccessInfo? Project { get; set; } = new(ProjectId, IsActive: true, IsDeleted: false);
    public ProjectMembership? Membership { get; set; }
    public List<string> AllPermissions { get; } = new();
    public List<string> RolePermissions { get; } = new();
    public List<DirectPermissionAssignment> Direct { get; } = new();

    public Exception? ThrowOnRead { get; set; }
    public Action? OnRead { get; set; }
    public int Reads { get; private set; }

    private T Read<T>(T value)
    {
        Reads++;
        OnRead?.Invoke();
        if (ThrowOnRead is not null)
            throw ThrowOnRead;

        return value;
    }

    public Task<UserAccessState?> GetUserAccessStateAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(Read(User));

    public Task<bool> IsGlobalAdminAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(Read(GlobalAdmin));

    public Task<ProjectAccessInfo?> FindProjectAsync(string projectCode, CancellationToken cancellationToken = default)
        => Task.FromResult(Read(Project));

    public Task<ProjectMembership?> GetProjectMembershipAsync(Guid projectId, string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(Read(Membership));

    public Task<IReadOnlyCollection<string>> GetAllPermissionCodesAsync(Guid projectId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyCollection<string>>(Read(AllPermissions.ToList()));

    public Task<IReadOnlyCollection<string>> GetRolePermissionCodesAsync(string userId, Guid projectId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyCollection<string>>(Read(RolePermissions.ToList()));

    public Task<IReadOnlyCollection<DirectPermissionAssignment>> GetDirectPermissionsAsync(string userId, Guid projectId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyCollection<DirectPermissionAssignment>>(Read(Direct.ToList()));
}

public class FakeVersionStore : IPermissionVersionStore
{
    public long Version { get; set; }
    public Exception? Throw { get; set; }
    public List<string> Incremented { get; } = new();

    public Task<long> GetCombinedVersionAsync(IReadOnlyCollection<string> contextKeys, CancellationToken cancellationToken = default)
    {
        if (Throw is not null)
            throw Throw;

        return Task.FromResult(Version);
    }

    public Task IncrementAsync(string contextKey, CancellationToken cancellationToken = default)
    {
        Incremented.Add(contextKey);
        Version++;
        return Task.CompletedTask;
    }
}

public class FakeCache : IPermissionCacheService
{
    public Dictionary<string, CachedPermissionsModel> Entries { get; } = new();
    public Exception? Throw { get; set; }
    public List<string> Removed { get; } = new();

    private void ThrowIfConfigured()
    {
        if (Throw is not null)
            throw Throw;
    }

    public Task<CachedPermissionsModel?> GetAsync(string userId, string projectCode, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        Entries.TryGetValue(PermissionCacheKeys.Entry(userId, projectCode), out var model);
        return Task.FromResult(model);
    }

    public Task SetAsync(string userId, string projectCode, CachedPermissionsModel model, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        Entries[PermissionCacheKeys.Entry(userId, projectCode)] = model;
        return Task.CompletedTask;
    }

    public Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        Removed.Add($"user:{userId}");
        return Task.CompletedTask;
    }

    public Task RemoveProjectAsync(string projectCode, CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        Removed.Add($"project:{projectCode}");
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConfigured();
        Removed.Add("all");
        Entries.Clear();
        return Task.CompletedTask;
    }
}

public class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
