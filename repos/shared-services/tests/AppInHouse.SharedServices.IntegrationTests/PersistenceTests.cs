using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using AppInHouse.SharedServices.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.IntegrationTests;

public class SqlitePersistenceTests : IAsyncLifetime
{
    private PersistenceHost _host = null!;

    public async Task InitializeAsync() => _host = await PersistenceHost.CreateSqliteAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static ChannelConfiguration NewConfig(string name = "Main", string project = "p") =>
        ChannelConfiguration.CreateFirstVersion(project, ChannelType.Email, ProviderType.Smtp, name, """{"host":"h"}""", "cipher", Now);

    // ------------------------------------------------------------------------------------------------ the model

    [Fact]
    public void EveryTable_LivesInTheSharedSchema_WithTheDocumentedName()
    {
        using var scope = _host.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>().Model;

        var tables = model.GetEntityTypes().ToDictionary(e => e.ClrType.Name, e => (e.GetSchema(), e.GetTableName()));

        Assert.Equal(("shared", "channel_configurations"), tables[nameof(ChannelConfiguration)]);
        Assert.Equal(("shared", "api_keys"), tables[nameof(ApiKey)]);
        Assert.Equal(("shared", "stored_files"), tables[nameof(StoredFile)]);
        Assert.Equal(("shared", "delivery_logs"), tables[nameof(DeliveryLog)]);
        Assert.Equal(("shared", "media_logs"), tables[nameof(MediaLog)]);
        Assert.Equal(("shared", "audit_logs"), tables[nameof(AuditLog)]);
        Assert.Equal(("shared", "system_logs"), tables[nameof(SystemLog)]);
        Assert.Equal(7, tables.Count);
    }

    [Fact]
    public void TheColumns_FollowTheDocumentedSnakeCaseNames()
    {
        using var scope = _host.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>().Model;
        var table = StoreObjectIdentifier.Table("channel_configurations", "shared");

        string Column(string property) => model.FindEntityType(typeof(ChannelConfiguration))!.FindProperty(property)!.GetColumnName(table)!;

        Assert.Equal("project_code", Column(nameof(ChannelConfiguration.ProjectCode)));
        Assert.Equal("channel_type", Column(nameof(ChannelConfiguration.ChannelType)));
        Assert.Equal("provider_type", Column(nameof(ChannelConfiguration.ProviderType)));
        Assert.Equal("settings_json", Column(nameof(ChannelConfiguration.SettingsJson)));
        Assert.Equal("encrypted_secret", Column(nameof(ChannelConfiguration.EncryptedSecret)));
        Assert.Equal("last_test_status", Column(nameof(ChannelConfiguration.LastTestStatus)));
        Assert.Equal("created_at", Column(nameof(ChannelConfiguration.CreatedAt)));

        var keyTable = StoreObjectIdentifier.Table("api_keys", "shared");
        var keys = model.FindEntityType(typeof(ApiKey))!;
        Assert.Equal("hashed_key", keys.FindProperty(nameof(ApiKey.HashedKey))!.GetColumnName(keyTable));
        Assert.Equal("key_prefix", keys.FindProperty(nameof(ApiKey.KeyPrefix))!.GetColumnName(keyTable));
        Assert.Equal("is_revoked", keys.FindProperty(nameof(ApiKey.IsRevoked))!.GetColumnName(keyTable));
    }

    [Fact]
    public void ThereIsNoColumnThatCouldHoldAPlainTextSecret()
    {
        using var scope = _host.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>().Model;

        var columns = model.GetEntityTypes().SelectMany(e => e.GetProperties()).Select(p => p.Name).ToList();

        Assert.DoesNotContain(columns, c => c.Equals("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, c => c.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, c => c.Equals("ApiKey", StringComparison.OrdinalIgnoreCase));   // only its hash
        Assert.Contains(columns, c => c == nameof(ChannelConfiguration.EncryptedSecret));
    }

    // ------------------------------------------------------------------------------------------------ storing and reading

    [Fact]
    public async Task AConfiguration_RoundTrips_WithItsEnumsStoredAsNames()
    {
        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            db.ChannelConfigurations.Add(NewConfig());
            await db.SaveChangesAsync();
        }

        using var read = _host.CreateScope();
        var context = read.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        var row = await context.ChannelConfigurations.AsNoTracking().SingleAsync();
        Assert.Equal((ChannelType.Email, ProviderType.Smtp, ConfigurationStatus.Draft, 1), (row.ChannelType, row.ProviderType, row.Status, row.Version));
        Assert.Equal("cipher", row.EncryptedSecret);
        Assert.Equal("""{"host":"h"}""", row.SettingsJson);

        var raw = await context.Database.SqlQueryRaw<string>("SELECT channel_type || '/' || provider_type || '/' || status AS \"Value\" FROM channel_configurations").SingleAsync();
        Assert.Equal("Email/Smtp/Draft", raw);
    }

    [Fact]
    public async Task TheSameVersionTwice_IsRefusedByTheDatabase()
    {
        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        db.ChannelConfigurations.Add(NewConfig("Dup"));
        await db.SaveChangesAsync();

        db.ChannelConfigurations.Add(NewConfig("Dup"));   // version 1 again, same project, channel and name

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TheSameVersionNumberInAnotherSeries_IsFine()
    {
        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        db.ChannelConfigurations.AddRange(NewConfig("A"), NewConfig("B"), NewConfig("A", project: "other"));

        Assert.Equal(3, await db.SaveChangesAsync());
    }

    [Fact]
    public async Task TwoPeopleActivatingDifferentDrafts_AtTheSameTime_CannotBothSucceed()
    {
        // Version 1 is active; versions 2 and 3 are drafts. Two people each activate a different draft.
        using (var setup = _host.CreateScope())
        {
            var manager = setup.ServiceProvider.GetRequiredService<IChannelConfigManager>();
            var v1 = await manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Race", "{}", null));
            await manager.ActivateAsync(v1.Id);
            await manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Race", """{"n":2}""", null));
            await manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Race", """{"n":3}""", null));
        }

        using var first = _host.CreateScope();
        using var second = _host.CreateScope();
        var firstSeries = await first.ServiceProvider.GetRequiredService<IChannelConfigRepository>().GetSeriesAsync("p", ChannelType.Email, "Race");
        var secondSeries = await second.ServiceProvider.GetRequiredService<IChannelConfigRepository>().GetSeriesAsync("p", ChannelType.Email, "Race");

        // Both have read "version 1 is active" and both archive it.
        ChannelConfigurationVersioning.Activate(firstSeries, 2, Now);
        ChannelConfigurationVersioning.Activate(secondSeries, 3, Now);

        await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        using var read = _host.CreateScope();
        var statuses = (await read.ServiceProvider.GetRequiredService<SharedServicesDbContext>().ChannelConfigurations
            .Where(c => c.Name == "Race").OrderBy(c => c.Version).Select(c => c.Status).ToListAsync());
        Assert.Equal([ConfigurationStatus.Archived, ConfigurationStatus.Active, ConfigurationStatus.Draft], statuses);   // exactly one winner
    }

    [Fact]
    public async Task ApiKeys_AreFoundByPrefix_AndOnlyTheHashIsStored()
    {
        var (key, plain) = ApiKey.Generate("p", "Key", [ApiScopes.EmailSend], null, Now);
        using (var scope = _host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiKeyRepository>().AddAsync(key);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using var read = _host.CreateScope();
        var repository = read.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        var found = Assert.Single(await repository.FindByPrefixAsync(key.KeyPrefix));
        Assert.True(found.Matches(plain));
        Assert.Empty(await repository.FindByPrefixAsync("aih_live_nothere"));
        Assert.True(await repository.AnyAsync());

        var stored = await read.ServiceProvider.GetRequiredService<SharedServicesDbContext>().Database
            .SqlQueryRaw<string>("SELECT key_prefix || '|' || hashed_key || '|' || scopes AS \"Value\" FROM api_keys").SingleAsync();
        Assert.DoesNotContain(plain, stored);
        Assert.Contains(ApiKey.Hash(plain), stored);
    }

    [Fact]
    public async Task TheOtherTables_StoreAndReadTheirRows_IncludingTheFileReferenceOfAMediaLog()
    {
        var file = StoredFile.Create("p", Guid.NewGuid(), ProviderType.LocalDisk, "a.png", "image/png", 10, new string('a', 64), false, Now);
        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            db.StoredFiles.Add(file);
            db.MediaLogs.Add(MediaLog.Create("p", file.Id, "Upload", "Success", null, Now));
            db.DeliveryLogs.Add(DeliveryLog.Create("p", ChannelType.Telegram, "12345", DeliveryStatus.Failed, "chat not found", 80, Now));
            db.AuditLogs.Add(AuditLog.Create("admin", "SendTest", "ChannelConfiguration", "id", """{"ok":false}""", Now));
            db.SystemLogs.Add(SystemLog.Create("Error", "Health", "db slow", null, Now));
            await db.SaveChangesAsync();
        }

        using var read = _host.CreateScope();
        var context = read.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        Assert.Equal(file.ObjectKey, (await context.StoredFiles.SingleAsync()).ObjectKey);
        Assert.Equal(file.Id, (await context.MediaLogs.SingleAsync()).FileId);
        Assert.Equal(DeliveryStatus.Failed, (await context.DeliveryLogs.SingleAsync()).Status);
        Assert.Equal("SendTest", (await context.AuditLogs.SingleAsync()).Action);
        Assert.Equal("db slow", (await context.SystemLogs.SingleAsync()).Message);
    }

    [Fact]
    public async Task AuditEntries_AreSavedInTheSameUnitOfWorkAsTheChange()
    {
        using (var scope = _host.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IChannelConfigManager>();
            var saved = await manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Audit", "{}", "secret-value"));
            await manager.ActivateAsync(saved.Id);
        }

        using var read = _host.CreateScope();
        var audit = await read.ServiceProvider.GetRequiredService<SharedServicesDbContext>().AuditLogs.OrderBy(a => a.Action).ToListAsync();
        Assert.Equal(["ActivateVersion", "CreateConfig"], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.Equal("tests", a.Actor));
        Assert.All(audit, a => Assert.DoesNotContain("secret-value", a.DetailsJson));
    }
}

/// <summary>The same wiring on a real PostgreSQL server: the migration, jsonb and the constraints. Skipped without a server.</summary>
public class PostgresPersistenceTests
{
    [PostgresFact]
    public async Task TheMigration_CreatesTheSevenTablesOfTheSharedSchema_WithJsonbColumns()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();

        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'shared' ORDER BY table_name").ToListAsync();
        var jsonColumns = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name || '.' || column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'shared' AND data_type = 'jsonb' ORDER BY 1").ToListAsync();

        Assert.Equal(["api_keys", "audit_logs", "channel_configurations", "delivery_logs", "media_logs", "stored_files", "system_logs"], tables);
        Assert.Equal(["audit_logs.details_json", "channel_configurations.settings_json"], jsonColumns);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task TheWholeLifeCycle_WorksOnPostgres_AndTheSecretIsCiphertext()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        using var scope = host.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IChannelConfigManager>();

        var v1 = await manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Main", """{"host":"a","port":587}""", "Pg-S3cret-value"));
        var v2 = await manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Main", """{"host":"a","port":25}"""));
        await manager.ActivateAsync(v2.Id);
        var restored = await manager.RollbackAsync(new RollbackConfigurationCommand("p", ChannelType.Email, "Main", 1));

        Assert.Equal(1, restored.Version);
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        var statuses = await db.ChannelConfigurations.OrderBy(c => c.Version).Select(c => c.Status).ToListAsync();
        Assert.Equal([ConfigurationStatus.Active, ConfigurationStatus.Archived], statuses);

        var stored = await db.Database.SqlQueryRaw<string>("SELECT encrypted_secret AS \"Value\" FROM shared.channel_configurations WHERE version = 1").SingleAsync();
        Assert.DoesNotContain("Pg-S3cret-value", stored);
        Assert.Equal("Pg-S3cret-value", (await manager.GetActiveForRuntimeAsync("p", ChannelType.Email, "Main"))!.Secret);

        // jsonb answers JSON questions: the settings column really is JSON on the server.
        var host1 = await db.Database.SqlQueryRaw<string>("SELECT settings_json ->> 'host' AS \"Value\" FROM shared.channel_configurations WHERE version = 1").SingleAsync();
        Assert.Equal("a", host1);
    }

    [PostgresFact]
    public async Task TheSameVersionTwice_IsRefusedByThePostgresConstraint()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.ChannelConfigurations.Add(ChannelConfiguration.CreateFirstVersion("p", ChannelType.Email, ProviderType.Smtp, "Dup", "{}", null, now));
        await db.SaveChangesAsync();

        db.ChannelConfigurations.Add(ChannelConfiguration.CreateFirstVersion("p", ChannelType.Email, ProviderType.Smtp, "Dup", "{}", null, now));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("uq_channel_version", exception.InnerException!.Message);
    }
}
