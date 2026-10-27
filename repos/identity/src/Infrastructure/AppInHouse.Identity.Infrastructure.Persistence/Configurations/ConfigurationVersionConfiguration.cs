using AppInHouse.Identity.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

public class ConfigurationVersionConfiguration : IEntityTypeConfiguration<ConfigurationVersion>
{
    public void Configure(EntityTypeBuilder<ConfigurationVersion> builder)
    {
        builder.ToTable("ConfigurationVersions");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.VersionNumber).IsRequired();
        builder.Property(c => c.Description).IsRequired().HasMaxLength(ConfigurationVersion.DescriptionMaxLength);
        builder.Property(c => c.AppliedAt).IsRequired();

        builder.HasIndex(c => c.VersionNumber);
    }
}
