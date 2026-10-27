using AppInHouse.Identity.Application.Authorization;
using AppInHouse.Identity.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.UnitTests.Infrastructure;

public class PermissionCacheServiceTests
{
    private static PermissionCacheService Create(TimeSpan? ttl = null)
        => new(new MemoryCache(new MemoryCacheOptions()),
               Options.Create(new PermissionCacheOptions { Ttl = ttl ?? TimeSpan.FromMinutes(5) }));

    private static CachedPermissionsModel Model(long version = 1, params string[] permissions)
        => new(version, new HashSet<string>(permissions));

    [Fact]
    public async Task Get_ReturnsWhatWasSet()
    {
        var cache = Create();

        await cache.SetAsync("u1", "ERP", Model(3, "orders.create"));
        var model = await cache.GetAsync("u1", "ERP");

        Assert.NotNull(model);
        Assert.Equal(3, model.Version);
        Assert.Contains("orders.create", model.Permissions);
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenMissing()
        => Assert.Null(await Create().GetAsync("u1", "ERP"));

    [Fact]
    public async Task ProjectCode_IsCaseInsensitive()
    {
        var cache = Create();
        await cache.SetAsync("u1", "erp", Model());

        Assert.NotNull(await cache.GetAsync("u1", "ERP"));
    }

    [Fact]
    public async Task Set_Replaces_AnExistingEntry()
    {
        var cache = Create();
        await cache.SetAsync("u1", "ERP", Model(1, "a"));
        await cache.SetAsync("u1", "ERP", Model(2, "b"));

        var model = await cache.GetAsync("u1", "ERP");
        Assert.Equal(2, model!.Version);
        Assert.Contains("b", model.Permissions);
    }

    [Fact]
    public async Task RemoveUser_EvictsEveryProjectOfThatUser_Only()
    {
        var cache = Create();
        await cache.SetAsync("u1", "ERP", Model());
        await cache.SetAsync("u1", "CRM", Model());
        await cache.SetAsync("u2", "ERP", Model());

        await cache.RemoveUserAsync("u1");

        Assert.Null(await cache.GetAsync("u1", "ERP"));
        Assert.Null(await cache.GetAsync("u1", "CRM"));
        Assert.NotNull(await cache.GetAsync("u2", "ERP"));
    }

    [Fact]
    public async Task RemoveProject_EvictsEveryUserOfThatProject_Only()
    {
        var cache = Create();
        await cache.SetAsync("u1", "ERP", Model());
        await cache.SetAsync("u2", "erp", Model());
        await cache.SetAsync("u1", "CRM", Model());

        await cache.RemoveProjectAsync("Erp");

        Assert.Null(await cache.GetAsync("u1", "ERP"));
        Assert.Null(await cache.GetAsync("u2", "ERP"));
        Assert.NotNull(await cache.GetAsync("u1", "CRM"));
    }

    [Fact]
    public async Task Clear_EvictsEverything()
    {
        var cache = Create();
        await cache.SetAsync("u1", "ERP", Model());
        await cache.SetAsync("u2", "CRM", Model());

        await cache.ClearAsync();

        Assert.Null(await cache.GetAsync("u1", "ERP"));
        Assert.Null(await cache.GetAsync("u2", "CRM"));
    }

    [Fact]
    public async Task EntriesExpireAfterTheConfiguredTtl()
    {
        var cache = Create(TimeSpan.FromMilliseconds(50));
        await cache.SetAsync("u1", "ERP", Model());

        await Task.Delay(300);

        Assert.Null(await cache.GetAsync("u1", "ERP"));
    }

    [Fact]
    public async Task Removing_AfterAnEntryWasReSet_StillEvictsIt()
    {
        var cache = Create();
        await cache.SetAsync("u1", "ERP", Model(1));
        await cache.RemoveUserAsync("u1");
        await Task.Delay(100); // lets the asynchronous eviction callback of the first entry run
        await cache.SetAsync("u1", "ERP", Model(2));

        await cache.RemoveUserAsync("u1");

        Assert.Null(await cache.GetAsync("u1", "ERP"));
    }
}
