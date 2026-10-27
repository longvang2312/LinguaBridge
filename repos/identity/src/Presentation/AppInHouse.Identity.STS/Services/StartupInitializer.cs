using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using AppInHouse.Identity.Infrastructure.Persistence.Seeders;

namespace AppInHouse.Identity.STS.Services;

/// <summary>Optionally creates the database schema and runs the <see cref="DbSeeder"/> when the host starts.</summary>
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

        // Interim until EF Core migrations exist: creates the AppIdentityDbContext schema (identity + OpenIddict tables).
        if (_configuration.GetValue<bool>("Database:EnsureCreated"))
        {
            var context = scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>();
            await context.Database.EnsureCreatedAsync(cancellationToken);
        }

        if (_configuration.GetValue<bool>($"{DbSeederOptions.SectionName}:Enabled"))
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
            await seeder.SeedAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
