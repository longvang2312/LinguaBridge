using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Contexts;

/// <summary>Owns the audit trail and configuration version tables.</summary>
public class AppAuditDbContext : DbContext
{
    public AppAuditDbContext(DbContextOptions<AppAuditDbContext> options) : base(options)
    {
    }

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ConfigurationVersion> ConfigurationVersions => Set<ConfigurationVersion>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new AuditLogConfiguration());
        builder.ApplyConfiguration(new ConfigurationVersionConfiguration());
    }
}
