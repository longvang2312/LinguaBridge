using AppInHouse.SharedServices.Application;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Infrastructure;
using AppInHouse.SharedServices.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AppInHouse.SharedServices.IntegrationTests.Support;

/// <summary>The real application, infrastructure and persistence wiring (no web host) on a throw-away database.</summary>
public sealed class PersistenceHost : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ss-db-" + Guid.NewGuid().ToString("N"));
    private readonly Func<Task>? _cleanup;

    private PersistenceHost(ServiceProvider services, Func<Task>? cleanup)
    {
        Services = services;
        _cleanup = cleanup;
    }

    public ServiceProvider Services { get; }

    public IServiceScope CreateScope() => Services.CreateScope();

    private sealed class TestActor : ICurrentActor
    {
        public string Name => "tests";
    }

    private static async Task<PersistenceHost> BuildAsync(Dictionary<string, string?> settings, string folder, Func<Task>? cleanup)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSharedServicesApplication();
        services.AddSharedServicesInfrastructure(configuration, folder);
        services.AddSharedServicesPersistence(configuration);
        services.AddScoped<ICurrentActor, TestActor>();

        var provider = services.BuildServiceProvider();
        await provider.InitializeSharedServicesDatabaseAsync();
        return new PersistenceHost(provider, cleanup);
    }

    public static Task<PersistenceHost> CreateSqliteAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ss-db-" + Guid.NewGuid().ToString("N"));
        return BuildAsync(new()
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:SharedServicesDb"] = $"Data Source={Path.Combine(folder, "test.db")}",
            ["DataProtection:KeysPath"] = Path.Combine(folder, "keys"),
        }, folder, () =>
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (IOException) { }
            return Task.CompletedTask;
        });
    }

    /// <summary>A new, empty PostgreSQL database on the server in the given connection string; dropped on dispose.</summary>
    public static async Task<PersistenceHost> CreatePostgresAsync(string serverConnectionString)
    {
        var databaseName = "ss_test_" + Guid.NewGuid().ToString("N")[..12];
        var folder = Path.Combine(Path.GetTempPath(), "ss-pg-" + Guid.NewGuid().ToString("N"));

        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = "postgres" }.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = databaseName }.ConnectionString;
        return await BuildAsync(new()
        {
            ["Database:Provider"] = "Postgres",
            ["ConnectionStrings:SharedServicesDb"] = connection,
            ["DataProtection:KeysPath"] = Path.Combine(folder, "keys"),
        }, folder, async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (IOException) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        if (_cleanup is not null) await _cleanup();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}

/// <summary>
/// Runs only when <c>SHARED_SERVICES_TEST_POSTGRES</c> holds a connection string to a PostgreSQL server (with rights to create
/// databases), for example <c>Host=localhost;Port=5432;Username=postgres;Password=postgres</c>. Otherwise it is skipped, so a
/// plain <c>dotnet test</c> needs no server.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "SHARED_SERVICES_TEST_POSTGRES";

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable(Variable);

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ServerConnectionString))
            Skip = $"Set {Variable} to a PostgreSQL connection string to run this test.";
    }
}
