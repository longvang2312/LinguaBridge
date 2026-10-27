using AppInHouse.Identity.Infrastructure.Persistence.Authorization;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.UnitTests.Infrastructure;

public class PermissionVersionStoreTests
{
    private static readonly string[] Keys = { "Global", "User:u1", "Project:ERP" };

    // ---- Provider without set-based updates (InMemory) ----

    private static Func<PermissionVersionStore> InMemoryStores()
    {
        var options = new DbContextOptionsBuilder<AppAuthDbContext>().UseInMemoryDatabase("versions-" + Guid.NewGuid()).Options;
        return () => new PermissionVersionStore(new AppAuthDbContext(options));
    }

    [Fact]
    public async Task InMemory_MissingKeysCountAsZero()
        => Assert.Equal(0, await InMemoryStores()().GetCombinedVersionAsync(Keys));

    [Fact]
    public async Task InMemory_Increment_CreatesThenIncrements_AndCombinedVersionSumsRequestedKeysOnly()
    {
        var create = InMemoryStores();

        await create().IncrementAsync("User:u1");
        await create().IncrementAsync("User:u1");
        await create().IncrementAsync("Project:ERP");
        await create().IncrementAsync("User:someone-else");

        Assert.Equal(3, await create().GetCombinedVersionAsync(Keys));
    }

    // ---- Relational provider (SQLite): exercises the atomic UPDATE path ----

    private sealed class SqliteDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"permission-versions-{Guid.NewGuid():N}.db");
        private readonly DbContextOptions<AppAuthDbContext> _options;

        public SqliteDatabase()
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();
            _options = new DbContextOptionsBuilder<AppAuthDbContext>().UseSqlite(connectionString).Options;

            using var context = new AppAuthDbContext(_options);
            context.Database.EnsureCreated();
        }

        public PermissionVersionStore NewStore() => new(new AppAuthDbContext(_options));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
                File.Delete(file);
        }
    }

    [Fact]
    public async Task Sqlite_Increment_CreatesThenIncrementsAtomically()
    {
        using var db = new SqliteDatabase();

        Assert.Equal(0, await db.NewStore().GetCombinedVersionAsync(Keys));

        await db.NewStore().IncrementAsync("User:u1");
        Assert.Equal(1, await db.NewStore().GetCombinedVersionAsync(Keys));

        await db.NewStore().IncrementAsync("User:u1");
        await db.NewStore().IncrementAsync("Global");
        Assert.Equal(3, await db.NewStore().GetCombinedVersionAsync(Keys));
        Assert.Equal(0, await db.NewStore().GetCombinedVersionAsync(new[] { "User:other" }));
    }

    [Fact]
    public async Task Sqlite_ConcurrentIncrements_NeverLoseAnUpdate()
    {
        using var db = new SqliteDatabase();
        const int writers = 24;

        // The key does not exist yet, so this also races the first INSERT.
        await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(() => db.NewStore().IncrementAsync("User:u1"))));

        Assert.Equal(writers, await db.NewStore().GetCombinedVersionAsync(new[] { "User:u1" }));
    }

    [Fact]
    public async Task Sqlite_Reads_SeeChangesMadeByAnotherContext()
    {
        using var db = new SqliteDatabase();
        var reader = db.NewStore();
        Assert.Equal(0, await reader.GetCombinedVersionAsync(Keys));

        await db.NewStore().IncrementAsync("User:u1");

        // The same (long-lived) store instance must not serve a cached value.
        Assert.Equal(1, await reader.GetCombinedVersionAsync(Keys));
    }

    [Fact]
    public async Task BlankContextKey_IsRejected()
        => await Assert.ThrowsAnyAsync<ArgumentException>(() => InMemoryStores()().IncrementAsync(" "));
}
