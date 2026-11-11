using AppInHouse.SharedServices.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.SharedServices.Persistence.Configurations;

// Tables of the "shared" schema (see docs/architecture/shared_services_design.md, section 3). Column names are snake_case by the
// naming convention registered with the context; enums are stored as their names so the rows stay readable.

internal sealed class ChannelConfigurationConfiguration : IEntityTypeConfiguration<ChannelConfiguration>
{
    public void Configure(EntityTypeBuilder<ChannelConfiguration> builder)
    {
        builder.ToTable("channel_configurations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(c => c.ChannelType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.ProviderType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Version).IsRequired();

        // The status doubles as the concurrency token: two people archiving the same active version at once cannot both succeed,
        // so two versions never end up active by a race.
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired().IsConcurrencyToken();

        builder.Property(c => c.SettingsJson).IsRequired();
        builder.Property(c => c.EncryptedSecret);
        builder.Property(c => c.LastTestStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(c => new { c.ProjectCode, c.ChannelType, c.Name, c.Version }).IsUnique().HasDatabaseName("uq_channel_version");
    }
}

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");
        builder.HasKey(k => k.Id);

        builder.Property(k => k.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(k => k.Name).HasMaxLength(100).IsRequired();
        builder.Property(k => k.KeyPrefix).HasMaxLength(16).IsRequired();
        builder.Property(k => k.HashedKey).HasMaxLength(128).IsRequired();
        builder.Property(k => k.Scopes).HasMaxLength(255).IsRequired();
        builder.Ignore(k => k.ScopeList);

        builder.HasIndex(k => k.KeyPrefix);
    }
}

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.ToTable("stored_files");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(f => f.StorageProvider).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(f => f.ObjectKey).HasMaxLength(500).IsRequired();
        builder.Property(f => f.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(f => f.MimeType).HasMaxLength(100).IsRequired();
        builder.Property(f => f.FileHashSha256).HasMaxLength(64).IsRequired();

        builder.HasIndex(f => new { f.ProjectCode, f.ObjectKey }).IsUnique();
    }
}

internal sealed class DeliveryLogConfiguration : IEntityTypeConfiguration<DeliveryLog>
{
    public void Configure(EntityTypeBuilder<DeliveryLog> builder)
    {
        builder.ToTable("delivery_logs");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(l => l.ChannelType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(l => l.Recipient).HasMaxLength(255).IsRequired();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(l => new { l.ProjectCode, l.CreatedAt });
    }
}

internal sealed class MediaLogConfiguration : IEntityTypeConfiguration<MediaLog>
{
    public void Configure(EntityTypeBuilder<MediaLog> builder)
    {
        builder.ToTable("media_logs");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(l => l.Action).HasMaxLength(50).IsRequired();
        builder.Property(l => l.Status).HasMaxLength(20).IsRequired();

        builder.HasOne<StoredFile>().WithMany().HasForeignKey(l => l.FileId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(l => new { l.ProjectCode, l.CreatedAt });
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Actor).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Action).HasMaxLength(50).IsRequired();
        builder.Property(l => l.ResourceType).HasMaxLength(50).IsRequired();
        builder.Property(l => l.ResourceId).HasMaxLength(100).IsRequired();

        builder.HasIndex(l => l.CreatedAt);
    }
}

internal sealed class SystemLogConfiguration : IEntityTypeConfiguration<SystemLog>
{
    public void Configure(EntityTypeBuilder<SystemLog> builder)
    {
        builder.ToTable("system_logs");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Level).HasMaxLength(20).IsRequired();
        builder.Property(l => l.Category).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Message).IsRequired();

        builder.HasIndex(l => l.CreatedAt);
    }
}
