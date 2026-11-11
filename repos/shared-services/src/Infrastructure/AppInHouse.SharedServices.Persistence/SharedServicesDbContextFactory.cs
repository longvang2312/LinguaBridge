using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AppInHouse.SharedServices.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>, so migrations can be added without building a host. Adding a migration never connects; the
/// connection string matters only for <c>dotnet ef database update</c> (override it with <c>--connection</c>).
/// Migrations are for PostgreSQL; SQLite creates its tables from the model.
/// </summary>
public sealed class SharedServicesDbContextFactory : IDesignTimeDbContextFactory<SharedServicesDbContext>
{
    public SharedServicesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SharedServicesDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=SharedServices_Design;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>()
            .Options;

        return new SharedServicesDbContext(options);
    }
}
