using AppInHouse.Identity.Infrastructure.Identity.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.Identity.Infrastructure.Persistence.Configurations;

/// <summary>Extra column configuration for <see cref="ApplicationRole"/> (owned by <c>AppIdentityDbContext</c>).</summary>
public class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.Property(r => r.Description).HasMaxLength(ApplicationRole.DescriptionMaxLength);
        builder.Property(r => r.CreatedAt).IsRequired();
    }
}
