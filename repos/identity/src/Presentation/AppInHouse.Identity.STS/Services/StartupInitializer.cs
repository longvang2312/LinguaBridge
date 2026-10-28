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
                // The identity tables come first: the authorization tables reference them.
                await identityContext.Database.MigrateAsync(cancellationToken);
                await scope.ServiceProvider.GetRequiredService<AppAuthDbContext>().Database.MigrateAsync(cancellationToken);
                await scope.ServiceProvider.GetRequiredService<AppAuditDbContext>().Database.MigrateAsync(cancellationToken);
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
