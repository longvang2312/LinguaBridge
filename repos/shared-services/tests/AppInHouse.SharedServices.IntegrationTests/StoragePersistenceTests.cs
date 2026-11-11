using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using AppInHouse.SharedServices.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>What the two storage repositories must do on a real database. Run on SQLite here and, with a server, on PostgreSQL.</summary>
internal static class StorageRepositoryChecks
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    public static StoredFile File(string project, string name, DateTimeOffset at, ProviderType provider = ProviderType.LocalDisk, string mime = "image/png") =>
        StoredFile.Create(project, Guid.NewGuid(), provider, name, mime, 1234, new string('a', 64), isPublic: name.StartsWith("pub", StringComparison.Ordinal), at);

    public static async Task SaveAsync(PersistenceHost host, params object[] rows)
    {
        using var scope = host.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        var logs = scope.ServiceProvider.GetRequiredService<IMediaLogRepository>();
        foreach (var row in rows)
        {
            if (row is StoredFile file) files.Add(file);
            else logs.Add((MediaLog)row);
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    public static async Task AFileRoundTrips(PersistenceHost host)
    {
        var file = File("p", "Holiday.png", T0.AddTicks(1234560), ProviderType.S3);
        await SaveAsync(host, file);

        using var scope = host.CreateScope();
        var read = await scope.ServiceProvider.GetRequiredService<IStoredFileRepository>().GetByIdAsync(file.Id);

        Assert.NotNull(read);
        Assert.Equal((file.ProjectCode, file.StorageProfileId, ProviderType.S3, file.ObjectKey, "Holiday.png", "image/png", 1234L, file.FileHashSha256, false),
            (read!.ProjectCode, read.StorageProfileId, read.StorageProvider, read.ObjectKey, read.OriginalFileName, read.MimeType, read.FileSizeBytes, read.FileHashSha256, read.IsPublic));
        Assert.Equal(T0.AddTicks(1234560), read.CreatedAt);   // every microsecond of the time survives (PostgreSQL keeps microseconds)
        Assert.Equal(file.ThumbnailObjectKey, read.ThumbnailObjectKey);
    }

    public static async Task EveryStorageProvider_IsStoredAndRead(PersistenceHost host)
    {
        var rows = new[] { ProviderType.LocalDisk, ProviderType.S3, ProviderType.Mock }.Select(p => File("p", p + ".png", T0, p)).ToArray();
        await SaveAsync(host, [.. rows]);

        using var scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        foreach (var row in rows) Assert.Equal(row.StorageProvider, (await repository.GetByIdAsync(row.Id))!.StorageProvider);
    }

    public static async Task TheList_IsNewestFirst_PerProject_WithLimitAndOffset(PersistenceHost host)
    {
        await SaveAsync(host,
            File("a", "oldest.png", T0), File("b", "other.png", T0.AddMinutes(3)), File("a", "middle.png", T0.AddMinutes(5)),
            File("a", "newest.png", T0.AddMinutes(10).AddMilliseconds(5)), File("a", "newer.png", T0.AddMinutes(10)));

        using var scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
        string[] Names(IReadOnlyList<StoredFile> rows) => [.. rows.Select(f => f.OriginalFileName)];

        Assert.Equal(["newest.png", "newer.png", "middle.png", "oldest.png"], Names(await repository.ListAsync("a", 10, 0)));   // fractions of a second sort correctly
        Assert.Equal(["newer.png", "middle.png"], Names(await repository.ListAsync("a", 2, 1)));
        Assert.Equal(["other.png"], Names(await repository.ListAsync("b", 10, 0)));
        Assert.Equal(5, (await repository.ListAsync(null, 50, 0)).Count);
        Assert.Empty(await repository.ListAsync("nobody", 10, 0));
        Assert.Empty(await repository.ListAsync("a", 10, 4));
    }

    public static async Task AFileIsRemoved_ButItsLogsStay_WithoutThePointer(PersistenceHost host)
    {
        var file = File("p", "a.png", T0);
        var log = MediaLog.Create("p", file.Id, "Upload", "Success", null, T0);
        await SaveAsync(host, file, log);

        using (var scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IStoredFileRepository>();
            repository.Remove((await repository.GetByIdAsync(file.Id))!);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using var read = host.CreateScope();
        Assert.Null(await read.ServiceProvider.GetRequiredService<IStoredFileRepository>().GetByIdAsync(file.Id));
        var kept = Assert.Single(await read.ServiceProvider.GetRequiredService<IMediaLogRepository>().ListRecentAsync("p", 10));
        Assert.Equal(("Upload", "Success", (Guid?)null), (kept.Action, kept.Status, kept.FileId));   // the log outlives the file
    }

    public static async Task TheObjectKeyOfAProject_IsUnique(PersistenceHost host)
    {
        var file = File("p", "a.png", T0);
        await SaveAsync(host, file);
        var twin = StoredFile.Create("p", Guid.NewGuid(), ProviderType.LocalDisk, "b.png", "image/png", 1, new string('b', 64), false, T0);
        typeof(StoredFile).GetProperty(nameof(StoredFile.ObjectKey))!.SetValue(twin, file.ObjectKey);   // the same key in the same project

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => SaveAsync(host, twin));
    }

    public static async Task TheLogs_AreNewestFirst_PerProject_AndLimited(PersistenceHost host)
    {
        await SaveAsync(host,
            MediaLog.Create("a", null, "Upload", "Success", null, T0),
            MediaLog.Create("b", null, "Upload", "Success", null, T0.AddMinutes(1)),
            MediaLog.Create("a", null, "Download", "Failed", "The file is no longer in the storage.", T0.AddMinutes(2).AddMilliseconds(3)),
            MediaLog.Create("a", null, "Delete", "Success", null, T0.AddMinutes(2)));

        using var scope = host.CreateScope();
        var logs = scope.ServiceProvider.GetRequiredService<IMediaLogRepository>();

        Assert.Equal(["Download", "Delete", "Upload"], (await logs.ListRecentAsync("a", 10)).Select(l => l.Action));
        Assert.Equal(["Download"], (await logs.ListRecentAsync("a", 1)).Select(l => l.Action));
        Assert.Equal(4, (await logs.ListRecentAsync(null, 10)).Count);
        var failed = (await logs.ListRecentAsync("a", 1))[0];
        Assert.Equal(("Failed", "The file is no longer in the storage.", T0.AddMinutes(2).AddMilliseconds(3)), (failed.Status, failed.ErrorMessage, failed.CreatedAt));
    }
}

public class SqliteStoragePersistenceTests : IAsyncLifetime
{
    private PersistenceHost _host = null!;

    public async Task InitializeAsync() => _host = await PersistenceHost.CreateSqliteAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact] public Task AFileRoundTrips() => StorageRepositoryChecks.AFileRoundTrips(_host);
    [Fact] public Task EveryStorageProvider_IsStoredAndRead() => StorageRepositoryChecks.EveryStorageProvider_IsStoredAndRead(_host);
    [Fact] public Task TheList_IsNewestFirst_PerProject_WithLimitAndOffset() => StorageRepositoryChecks.TheList_IsNewestFirst_PerProject_WithLimitAndOffset(_host);
    [Fact] public Task AFileIsRemoved_ButItsLogsStay_WithoutThePointer() => StorageRepositoryChecks.AFileIsRemoved_ButItsLogsStay_WithoutThePointer(_host);
    [Fact] public Task TheObjectKeyOfAProject_IsUnique() => StorageRepositoryChecks.TheObjectKeyOfAProject_IsUnique(_host);
    [Fact] public Task TheLogs_AreNewestFirst_PerProject_AndLimited() => StorageRepositoryChecks.TheLogs_AreNewestFirst_PerProject_AndLimited(_host);
}

/// <summary>The same on a real PostgreSQL server. Skipped without one.</summary>
public class PostgresStoragePersistenceTests
{
    private static async Task RunAsync(Func<PersistenceHost, Task> check)
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await check(host);
    }

    [PostgresFact] public Task AFileRoundTrips() => RunAsync(StorageRepositoryChecks.AFileRoundTrips);
    [PostgresFact] public Task EveryStorageProvider_IsStoredAndRead() => RunAsync(StorageRepositoryChecks.EveryStorageProvider_IsStoredAndRead);
    [PostgresFact] public Task TheList_IsNewestFirst_PerProject_WithLimitAndOffset() => RunAsync(StorageRepositoryChecks.TheList_IsNewestFirst_PerProject_WithLimitAndOffset);
    [PostgresFact] public Task AFileIsRemoved_ButItsLogsStay_WithoutThePointer() => RunAsync(StorageRepositoryChecks.AFileIsRemoved_ButItsLogsStay_WithoutThePointer);
    [PostgresFact] public Task TheObjectKeyOfAProject_IsUnique() => RunAsync(StorageRepositoryChecks.TheObjectKeyOfAProject_IsUnique);
    [PostgresFact] public Task TheLogs_AreNewestFirst_PerProject_AndLimited() => RunAsync(StorageRepositoryChecks.TheLogs_AreNewestFirst_PerProject_AndLimited);
}
