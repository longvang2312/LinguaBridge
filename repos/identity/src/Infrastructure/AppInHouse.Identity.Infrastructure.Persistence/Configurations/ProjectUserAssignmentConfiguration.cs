using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

public class ProjectUserAssignmentConfiguration : IEntityTypeConfiguration<ProjectUserAssignment>
{
    public void Configure(EntityTypeBuilder<ProjectUserAssignment> builder)
    {
        builder.ToTable("ProjectUserAssignments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.UserId).IsRequired().HasMaxLength(ProjectUserAssignment.UserIdMaxLength);
        builder.Property(a => a.IsProjectAdmin).IsRequired();
        builder.Property(a => a.IsBlocked).IsRequired();
        builder.Property(a => a.AssignedAt).IsRequired();

        builder.HasOne(a => a.Project)
            .WithMany()
            .HasForeignKey(a => a.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.ProjectId, a.UserId }).IsUnique();
        builder.HasIndex(a => a.UserId);
    }
}
