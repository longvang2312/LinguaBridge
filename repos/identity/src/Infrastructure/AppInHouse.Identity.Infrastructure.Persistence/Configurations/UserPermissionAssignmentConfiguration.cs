using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

public class UserPermissionAssignmentConfiguration : IEntityTypeConfiguration<UserPermissionAssignment>
{
    public void Configure(EntityTypeBuilder<UserPermissionAssignment> builder)
    {
        builder.ToTable("UserPermissionAssignments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.UserId).IsRequired().HasMaxLength(UserPermissionAssignment.UserIdMaxLength);
        builder.Property(a => a.IsRevoked).IsRequired();
        builder.Property(a => a.AssignedAt).IsRequired();

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict (not cascade): Permission also references Project, which would create multiple cascade paths on SQL Server.
        builder.HasOne(a => a.Permission)
            .WithMany()
            .HasForeignKey(a => a.PermissionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Project)
            .WithMany()
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.UserId, a.PermissionId, a.ProjectId }).IsUnique();
        builder.HasIndex(a => new { a.UserId, a.ProjectId });
        builder.HasIndex(a => a.PermissionId);
        builder.HasIndex(a => a.ProjectId);
    }
}
