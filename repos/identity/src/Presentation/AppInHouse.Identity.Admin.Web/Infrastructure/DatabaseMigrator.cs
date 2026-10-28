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

        await identityContext.Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<AppAuthDbContext>().Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<AppAuditDbContext>().Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
