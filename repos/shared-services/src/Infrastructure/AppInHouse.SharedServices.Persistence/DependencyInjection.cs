using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Application.Email;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.Persistence;

public static class DependencyInjection
{
    public const string PostgresProvider = "Postgres";
    public const string SqliteProvider = "Sqlite";
    public const string ConnectionStringName = "SharedServicesDb";

    /// <summary>
    /// <c>Database:Provider</c> is "Postgres" (production) or "Sqlite" (the default: development and tests, no server needed);
    /// the connection string is <c>ConnectionStrings:SharedServicesDb</c>.
    /// </summary>
    public static IServiceCollection AddSharedServicesPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? SqliteProvider;
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not set.");

        if (provider.Equals(SqliteProvider, StringComparison.OrdinalIgnoreCase)) EnsureSqliteFolderExists(connectionString);

        services.AddDbContext<SharedServicesDbContext>(options =>
        {
            if (provider.Equals(PostgresProvider, StringComparison.OrdinalIgnoreCase)) options.UseNpgsql(connectionString);
            else if (provider.Equals(SqliteProvider, StringComparison.OrdinalIgnoreCase)) options.UseSqlite(connectionString);
            else throw new InvalidOperationException($"Database:Provider '{provider}' is not supported (use Postgres or Sqlite).");

            options.UseSnakeCaseNamingConvention();
            options.ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>();
        });

        services.AddScoped<IChannelConfigRepository, ChannelConfigRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IDeliveryLogRepository, DeliveryLogRepository>();
        services.AddScoped<IStoredFileRepository, StoredFileRepository>();
        services.AddScoped<IMediaLogRepository, MediaLogRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAuditLogWriter, AuditLogWriter>();
        return services;
    }

    // SQLite creates the file but not the folder around it.
    private static void EnsureSqliteFolderExists(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.StartsWith(':') || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return;

        var folder = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
    }

    /// <summary>
    /// Brings the database up to date: applies the migrations on PostgreSQL, creates the tables on SQLite (which has no
    /// migrations here because its column types differ). Safe to run on every start.
    /// </summary>
    public static async Task InitializeSharedServicesDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        if (db.Database.IsNpgsql()) await db.Database.MigrateAsync(cancellationToken);
        else await db.Database.EnsureCreatedAsync(cancellationToken);
    }
}
