using AppInHouse.Identity.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.ProjectCode).IsRequired().HasMaxLength(Project.ProjectCodeMaxLength);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(Project.NameMaxLength);
        builder.Property(p => p.Description).HasMaxLength(Project.DescriptionMaxLength);
        builder.Property(p => p.IsActive).IsRequired();
        builder.Property(p => p.IsDeleted).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.ConcurrencyToken)
            .IsRequired()
            .HasMaxLength(Project.ConcurrencyTokenMaxLength)
            .IsConcurrencyToken();

        // Filtered: the code of a soft-deleted project can be reused.
        builder.HasIndex(p => p.ProjectCode).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}
