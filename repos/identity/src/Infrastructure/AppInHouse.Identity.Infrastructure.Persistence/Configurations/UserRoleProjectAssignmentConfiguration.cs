using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

public class UserRoleProjectAssignmentConfiguration : IEntityTypeConfiguration<UserRoleProjectAssignment>
{
    public void Configure(EntityTypeBuilder<UserRoleProjectAssignment> builder)
    {
        builder.ToTable("UserRoleProjectAssignments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.UserId).IsRequired().HasMaxLength(UserRoleProjectAssignment.UserIdMaxLength);
        builder.Property(a => a.RoleId).IsRequired().HasMaxLength(UserRoleProjectAssignment.RoleIdMaxLength);
        builder.Property(a => a.AssignedAt).IsRequired();

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApplicationRole>()
            .WithMany()
            .HasForeignKey(a => a.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Project)
            .WithMany()
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.UserId, a.RoleId, a.ProjectId }).IsUnique();
        builder.HasIndex(a => new { a.UserId, a.ProjectId });
        builder.HasIndex(a => a.RoleId);
        builder.HasIndex(a => a.ProjectId);
    }
}
