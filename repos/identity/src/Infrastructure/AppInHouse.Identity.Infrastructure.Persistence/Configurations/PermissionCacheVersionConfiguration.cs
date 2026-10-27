using AppInHouse.Identity.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

public class PermissionCacheVersionConfiguration : IEntityTypeConfiguration<PermissionCacheVersion>
{
    public void Configure(EntityTypeBuilder<PermissionCacheVersion> builder)
    {
        builder.ToTable("PermissionCacheVersions");
        builder.HasKey(v => v.ContextKey);

        builder.Property(v => v.ContextKey)
            .HasMaxLength(PermissionCacheVersion.ContextKeyMaxLength)
            .ValueGeneratedNever();
        builder.Property(v => v.VersionNumber).IsRequired();
        builder.Property(v => v.LastInvalidatedAt).IsRequired();
    }
}
