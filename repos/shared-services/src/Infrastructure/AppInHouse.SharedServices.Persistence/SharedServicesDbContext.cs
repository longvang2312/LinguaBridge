using System.Globalization;
using AppInHouse.SharedServices.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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

        if (Database.IsSqlite()) StoreTimesAsSortableText(modelBuilder);
    }

    // SQLite cannot order or compare a DateTimeOffset in a query ("the most recent first" would fail). Store every time as UTC text with a
    // fixed layout instead: it reads back the same, and as text it sorts in time order. The layout matches what EF writes by default
    // for a UTC time, so a database created before this existed is still readable.
    private static void StoreTimesAsSortableText(ModelBuilder modelBuilder)
    {
        const string layout = "yyyy-MM-dd HH:mm:ss.fffffff";
        var culture = CultureInfo.InvariantCulture;
        var required = new ValueConverter<DateTimeOffset, string>(
            v => v.UtcDateTime.ToString(layout, culture) + "+00:00",
            s => DateTimeOffset.Parse(s, culture, DateTimeStyles.AssumeUniversal));
        var optional = new ValueConverter<DateTimeOffset?, string?>(
            v => v == null ? null : v.Value.UtcDateTime.ToString(layout, culture) + "+00:00",
            s => s == null ? null : DateTimeOffset.Parse(s, culture, DateTimeStyles.AssumeUniversal));

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()).ToList())
        {
            if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(required);
            else if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(optional);
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
