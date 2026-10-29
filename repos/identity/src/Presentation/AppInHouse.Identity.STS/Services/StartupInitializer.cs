using Microsoft.Data.SqlClient;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using AppInHouse.Identity.Infrastructure.Persistence.Seeders;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.STS.Services;

/// <summary>Optionally applies the EF Core migrations and runs the <see cref="DbSeeder"/> when the host starts.</summary>
public class StartupInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;

    public StartupInitializer(IServiceProvider services, IConfiguration configuration)
    {
        _services = services;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _services.CreateAsyncScope();

        if (_configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            var identityContext = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

            // Migrations only exist for relational providers (SQL Server); an in-memory database has nothing to migrate.
            if (identityContext.Database.IsRelational())
            {
                var authContext = scope.ServiceProvider.GetRequiredService<AppAuthDbContext>();
                var auditContext = scope.ServiceProvider.GetRequiredService<AppAuditDbContext>();

                // The identity tables come first (the authorization tables reference them); the lock keeps other hosts out meanwhile.
                await WithMigrationLockAsync(identityContext.Database.GetConnectionString()!, async () =>
                {
                    await identityContext.Database.MigrateAsync(cancellationToken);
                    await authContext.Database.MigrateAsync(cancellationToken);
                    await auditContext.Database.MigrateAsync(cancellationToken);
                }, cancellationToken);
            }
        }
        else if (_configuration.GetValue<bool>("Database:EnsureCreated"))
        {
            // Legacy / in-memory fallback (creates only the AppIdentityDbContext schema, without migrations history).
            var identityContext = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            await identityContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        if (_configuration.GetValue<bool>($"{DbSeederOptions.SectionName}:Enabled"))
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
            await seeder.SeedAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> while holding a SQL Server application lock, so that several hosts starting at the same
    /// time (STS, Admin.Web, ...) migrate one after the other instead of racing to create the same tables.
    /// </summary>
    private static async Task WithMigrationLockAsync(string connectionString, Func<Task> work, CancellationToken cancellationToken)
    {
        // A separate connection to master: the target database may not exist yet, and application locks are scoped per database.
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        {
            await using (var acquire = connection.CreateCommand())
            {
                acquire.CommandText =
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'AppInHouse.Identity.Migrations', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 120000; SELECT @r;";
                if (Convert.ToInt32(await acquire.ExecuteScalarAsync(cancellationToken)) < 0)
                    throw new InvalidOperationException("Could not acquire the database migration lock within 120 seconds.");
            }

            try
            {
                await work();
            }
            finally
            {
                await using var release = connection.CreateCommand();
                release.CommandText = "EXEC sp_releaseapplock @Resource = N'AppInHouse.Identity.Migrations', @LockOwner = 'Session';";
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
