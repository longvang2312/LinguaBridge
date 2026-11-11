using AppInHouse.SharedServices.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AppInHouse.SharedServices.Persistence;

public static class Schemas
{
    public const string Shared = "shared";
}

public sealed class SharedServicesDbContext(DbContextOptions<SharedServicesDbContext> options) : DbContext(options)
{
    public DbSet<ChannelConfiguration> ChannelConfigurations => Set<ChannelConfiguration>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<DeliveryLog> DeliveryLogs => Set<DeliveryLog>();
    public DbSet<MediaLog> MediaLogs => Set<MediaLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();

    /// <summary>jsonb on PostgreSQL, plain text on SQLite (development and tests).</summary>
    internal bool UsesJsonb => Database.IsNpgsql();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schemas.Shared);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SharedServicesDbContext).Assembly, type => !type.IsAbstract);

        if (UsesJsonb)
        {
            modelBuilder.Entity<ChannelConfiguration>().Property(c => c.SettingsJson).HasColumnType("jsonb");
            modelBuilder.Entity<AuditLog>().Property(a => a.DetailsJson).HasColumnType("jsonb");
        }
    }
}

/// <summary>
/// The model differs per provider (jsonb or not), and EF Core's default cache key ignores the provider; a process that builds
/// both (a test run next to a design-time call) would otherwise get the wrong model.
/// </summary>
internal sealed class ProviderAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.Database.ProviderName, designTime);
}
