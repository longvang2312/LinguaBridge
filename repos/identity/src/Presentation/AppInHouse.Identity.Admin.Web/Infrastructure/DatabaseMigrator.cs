using Microsoft.Data.SqlClient;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Admin.Web.Infrastructure;

/// <summary>
/// Applies the EF Core migrations when the host starts (<c>Database:MigrateOnStartup</c>). The identity tables come first
/// because the authorization tables reference them.
/// </summary>
public sealed class DatabaseMigrator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;

    public DatabaseMigrator(IServiceProvider services, IConfiguration configuration)
    {
        _services = services;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue<bool>("Database:MigrateOnStartup"))
            return;

        await using var scope = _services.CreateAsyncScope();

        var identityContext = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();

        // Migrations only exist for relational providers; never fail on an in-memory database (tests).
        if (!identityContext.Database.IsRelational())
            return;

        var authContext = scope.ServiceProvider.GetRequiredService<AppAuthDbContext>();
        var auditContext = scope.ServiceProvider.GetRequiredService<AppAuditDbContext>();

        await WithMigrationLockAsync(identityContext.Database.GetConnectionString()!, async () =>
        {
            await identityContext.Database.MigrateAsync(cancellationToken);
            await authContext.Database.MigrateAsync(cancellationToken);
            await auditContext.Database.MigrateAsync(cancellationToken);
        }, cancellationToken);
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
