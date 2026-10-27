using AppInHouse.Identity.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

/// <remarks>
/// UserId / ProjectId are intentionally not foreign keys so audit records survive deletion of the actor or project.
/// </remarks>
public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.UserId).HasMaxLength(AuditLog.UserIdMaxLength);
        builder.Property(a => a.Action).IsRequired().HasMaxLength(AuditLog.ActionMaxLength);
        builder.Property(a => a.EntityName).IsRequired().HasMaxLength(AuditLog.EntityNameMaxLength);
        builder.Property(a => a.EntityId).IsRequired().HasMaxLength(AuditLog.EntityIdMaxLength);
        builder.Property(a => a.OldValuesJson);
        builder.Property(a => a.NewValuesJson);
        builder.Property(a => a.IpAddress).HasMaxLength(AuditLog.IpAddressMaxLength);
        builder.Property(a => a.Timestamp).IsRequired();

        builder.HasIndex(a => a.Timestamp);
        builder.HasIndex(a => a.UserId);
    }
}
