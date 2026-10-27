using AppInHouse.Identity.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions", t => t.HasCheckConstraint(
            "CK_Permissions_IsGlobal_ProjectId",
            "([IsGlobal] = 1 AND [ProjectId] IS NULL) OR ([IsGlobal] = 0 AND [ProjectId] IS NOT NULL)"));
        builder.HasKey(p => p.Id);

        builder.Property(p => p.PermissionCode).IsRequired().HasMaxLength(Permission.PermissionCodeMaxLength);
        builder.Property(p => p.DisplayName).IsRequired().HasMaxLength(Permission.DisplayNameMaxLength);
        builder.Property(p => p.Module).IsRequired().HasMaxLength(Permission.ModuleMaxLength);
        builder.Property(p => p.IsGlobal).IsRequired();
        builder.Property(p => p.IsActive).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();

        // ProjectId == null means a global permission.
        builder.HasOne(p => p.Project)
            .WithMany()
            .HasForeignKey(p => p.ProjectId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.PermissionCode).IsUnique();
        builder.HasIndex(p => p.ProjectId);
    }
}
