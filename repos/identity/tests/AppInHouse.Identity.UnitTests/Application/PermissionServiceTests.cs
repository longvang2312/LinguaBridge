using AppInHouse.Identity.Application.Authorization;
using AppInHouse.Identity.Application.Services;
using AppInHouse.Identity.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.Identity.UnitTests.Application;

public class PermissionServiceTests
{
    private const string User = "user-1";
    private const string Project = "ERP";

    private readonly FakeDataSource _data = new();
    private readonly FakeVersionStore _versions = new();
    private readonly FakeCache _cache = new();
    private readonly FakeTimeProvider _time = new();

    private PermissionService CreateService()
        => new(_data, _versions, _cache, NullLogger<PermissionService>.Instance, _time);

    private async Task<HashSet<string>> EffectiveAsync()
        => new(await CreateService().GetEffectivePermissionsAsync(User, Project), StringComparer.OrdinalIgnoreCase);

    // ---- Step 1: user ----

    [Fact]
    public async Task UnknownUser_HasNoPermissions()
    {
        _data.User = null;
        _data.RolePermissions.Add("orders.create");

        Assert.Empty(await EffectiveAsync());
    }

    [Fact]
    public async Task InactiveUser_HasNoPermissions()
    {
        _data.User = new UserAccessState(IsActive: false, LockoutEnd: null);
        _data.RolePermissions.Add("orders.create");

        Assert.Empty(await EffectiveAsync());
    }

    [Fact]
    public async Task LockedOutUser_HasNoPermissions_UntilLockoutEnds()
    {
        _data.User = new UserAccessState(true, _time.Now.AddMinutes(10));
        _data.RolePermissions.Add("orders.create");

        Assert.Empty(await EffectiveAsync());

        // Expired lockout: the cached empty result is stale only until the version changes.
        _time.Now = _time.Now.AddMinutes(11);
        _versions.Version++;
        Assert.Contains("orders.create", await EffectiveAsync());
    }

    // ---- Step 2: project ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InactiveOrDeletedProject_HasNoPermissions(bool isActive, bool isDeleted)
    {
        _data.Project = new ProjectAccessInfo(FakeDataSource.ProjectId, isActive, isDeleted);
        _data.RolePermissions.Add("orders.create");
        _data.Membership = new ProjectMembership(IsProjectAdmin: true, IsBlocked: false);
        _data.AllPermissions.Add("orders.create");

        Assert.Empty(await EffectiveAsync());
    }

    [Fact]
    public async Task MissingProject_HasNoPermissions()
    {
        _data.Project = null;
        _data.RolePermissions.Add("orders.create");

        Assert.Empty(await EffectiveAsync());
    }

    // ---- Step 3: membership ----

    [Fact]
    public async Task BlockedInProject_HasNoPermissions_EvenAsAdmin()
    {
        _data.Membership = new ProjectMembership(IsProjectAdmin: true, IsBlocked: true);
        _data.GlobalAdmin = true;
        _data.AllPermissions.Add("orders.create");
        _data.RolePermissions.Add("orders.create");

        Assert.Empty(await EffectiveAsync());
    }

    [Fact]
    public async Task ProjectAdmin_GetsEveryPermissionOfTheProject()
    {
        _data.Membership = new ProjectMembership(IsProjectAdmin: true, IsBlocked: false);
        _data.AllPermissions.AddRange(new[] { "orders.create", "orders.delete", "users.view" });

        Assert.Equal(new[] { "orders.create", "orders.delete", "users.view" }, (await EffectiveAsync()).Order());
    }

    [Fact]
    public async Task GlobalAdmin_GetsEveryPermission_WithoutProjectMembership()
    {
        _data.Membership = null;
        _data.GlobalAdmin = true;
        _data.AllPermissions.AddRange(new[] { "orders.create", "users.view" });

        Assert.Equal(new[] { "orders.create", "users.view" }, (await EffectiveAsync()).Order());
    }

    [Fact]
    public async Task GlobalAdmin_StillNeedsAnActiveProject()
    {
        _data.GlobalAdmin = true;
        _data.AllPermissions.Add("orders.create");
        _data.Project = new ProjectAccessInfo(FakeDataSource.ProjectId, IsActive: false, IsDeleted: false);

        Assert.Empty(await EffectiveAsync());
    }

    // ---- Steps 4-6: roles, direct grants, revocations ----

    [Fact]
    public async Task RegularUser_GetsTheUnionOfRolePermissions()
    {
        _data.Membership = new ProjectMembership(false, false);
        _data.RolePermissions.AddRange(new[] { "orders.create", "orders.view", "orders.view" });

        Assert.Equal(new[] { "orders.create", "orders.view" }, (await EffectiveAsync()).Order());
    }

    [Fact]
    public async Task UserWithoutRolesOrGrants_HasNoPermissions()
        => Assert.Empty(await EffectiveAsync());

    [Fact]
    public async Task DirectGrant_IsAddedToRolePermissions()
    {
        _data.RolePermissions.Add("orders.view");
        _data.Direct.Add(new DirectPermissionAssignment("orders.create", IsRevoked: false));

        Assert.Equal(new[] { "orders.create", "orders.view" }, (await EffectiveAsync()).Order());
    }

    [Fact]
    public async Task DirectRevocation_RemovesPermissionGrantedByRole()
    {
        _data.RolePermissions.AddRange(new[] { "orders.create", "orders.view" });
        _data.Direct.Add(new DirectPermissionAssignment("orders.create", IsRevoked: true));

        Assert.Equal(new[] { "orders.view" }, (await EffectiveAsync()).Order());
    }

    [Fact]
    public async Task Revocation_WinsOverDirectGrantOfTheSamePermission()
    {
        _data.Direct.Add(new DirectPermissionAssignment("orders.create", IsRevoked: false));
        _data.Direct.Add(new DirectPermissionAssignment("orders.create", IsRevoked: true));

        Assert.Empty(await EffectiveAsync());
    }

    [Fact]
    public async Task PermissionCodes_AreCaseInsensitive()
    {
        _data.RolePermissions.Add("Orders.Create");
        _data.Direct.Add(new DirectPermissionAssignment("ORDERS.CREATE", IsRevoked: true));

        Assert.Empty(await EffectiveAsync());
    }

    // ---- HasPermission ----

    [Fact]
    public async Task HasPermission_ReturnsTrueOnlyForEffectivePermissions()
    {
        _data.RolePermissions.Add("orders.create");
        var service = CreateService();

        Assert.True(await service.HasPermissionAsync(User, Project, "orders.create"));
        Assert.True(await service.HasPermissionAsync(User, Project, "ORDERS.CREATE"));
        Assert.False(await service.HasPermissionAsync(User, Project, "orders.delete"));
    }

    [Theory]
    [InlineData(null, "ERP", "a.b")]
    [InlineData("", "ERP", "a.b")]
    [InlineData("user-1", " ", "a.b")]
    [InlineData("user-1", "ERP", null)]
    [InlineData("user-1", "ERP", "  ")]
    public async Task HasPermission_DeniesBlankArguments_WithoutTouchingTheDataSource(string? user, string? project, string? permission)
    {
        _data.RolePermissions.Add("a.b");

        Assert.False(await CreateService().HasPermissionAsync(user!, project!, permission!));
        Assert.Equal(0, _data.Reads);
    }

    // ---- Caching & versioning ----

    [Fact]
    public async Task SecondCall_IsServedFromCache()
    {
        _data.RolePermissions.Add("orders.create");
        var service = CreateService();

        await service.HasPermissionAsync(User, Project, "orders.create");
        var readsAfterFirst = _data.Reads;
        await service.HasPermissionAsync(User, Project, "orders.create");

        Assert.True(readsAfterFirst > 0);
        Assert.Equal(readsAfterFirst, _data.Reads);
    }

    [Fact]
    public async Task VersionChange_ForcesRecomputation()
    {
        _data.RolePermissions.Add("orders.create");
        var service = CreateService();
        Assert.True(await service.HasPermissionAsync(User, Project, "orders.create"));

        _data.RolePermissions.Clear();
        Assert.True(await service.HasPermissionAsync(User, Project, "orders.create")); // still cached

        _versions.Version++;
        Assert.False(await service.HasPermissionAsync(User, Project, "orders.create"));
    }

    [Fact]
    public async Task ChangeDuringComputation_IsNotServedStaleOnTheNextCall()
    {
        _data.RolePermissions.Add("orders.create");
        // An admin change lands right after the role permissions were read (5th read: user, project, membership,
        // global-admin check, roles), i.e. while the permissions are still being computed.
        _data.OnRead = () =>
        {
            if (_data.Reads == 5)
            {
                _versions.Version = 1;
                _data.RolePermissions.Clear();
            }
        };
        var service = CreateService();

        // Computed from the old data, but stored under the version that was current BEFORE the change (0).
        Assert.True(await service.HasPermissionAsync(User, Project, "orders.create"));

        Assert.False(await service.HasPermissionAsync(User, Project, "orders.create")); // version 1 != 0 -> recomputed
    }

    [Fact]
    public async Task VersionIsPartOfTheCachedModel()
    {
        _versions.Version = 7;
        _data.RolePermissions.Add("orders.create");

        await EffectiveAsync();

        var entry = Assert.Single(_cache.Entries.Values);
        Assert.Equal(7, entry.Version);
        Assert.Contains("orders.create", entry.Permissions);
    }

    // ---- Fail-safe ----

    [Fact]
    public async Task DataSourceFailure_DeniesAccess()
    {
        _data.RolePermissions.Add("orders.create");
        _data.ThrowOnRead = new InvalidOperationException("database down");

        Assert.False(await CreateService().HasPermissionAsync(User, Project, "orders.create"));
    }

    [Fact]
    public async Task CacheFailure_DeniesAccess()
    {
        _data.RolePermissions.Add("orders.create");
        _cache.Throw = new InvalidOperationException("cache down");

        Assert.False(await CreateService().HasPermissionAsync(User, Project, "orders.create"));
    }

    [Fact]
    public async Task VersionStoreFailure_DeniesAccess()
    {
        _data.RolePermissions.Add("orders.create");
        _versions.Throw = new InvalidOperationException("version table unreachable");

        Assert.False(await CreateService().HasPermissionAsync(User, Project, "orders.create"));
    }

    [Fact]
    public async Task GetEffectivePermissions_PropagatesFailures()
    {
        _data.ThrowOnRead = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().GetEffectivePermissionsAsync(User, Project));
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _data.ThrowOnRead = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateService().HasPermissionAsync(User, Project, "orders.create", cts.Token));
    }
}
