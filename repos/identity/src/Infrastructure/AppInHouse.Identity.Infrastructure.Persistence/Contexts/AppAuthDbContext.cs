using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Contexts;

/// <summary>Owns the project / permission / assignment tables used for dynamic RBAC.</summary>
public class AppAuthDbContext : DbContext
{
    public AppAuthDbContext(DbContextOptions<AppAuthDbContext> options) : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<ProjectUserAssignment> ProjectUserAssignments => Set<ProjectUserAssignment>();
    public DbSet<UserRoleProjectAssignment> UserRoleProjectAssignments => Set<UserRoleProjectAssignment>();
    public DbSet<UserPermissionAssignment> UserPermissionAssignments => Set<UserPermissionAssignment>();
    public DbSet<PermissionCacheVersion> PermissionCacheVersions => Set<PermissionCacheVersion>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // AspNetUsers / AspNetRoles belong to AppIdentityDbContext. They are mapped here only so the foreign keys
        // from the assignment tables can be modelled; ExcludeFromMigrations keeps this context from creating them.
        builder.Entity<ApplicationUser>().ToTable("AspNetUsers", t => t.ExcludeFromMigrations());
        builder.Entity<ApplicationRole>().ToTable("AspNetRoles", t => t.ExcludeFromMigrations());

        builder.ApplyConfiguration(new ProjectConfiguration());
        builder.ApplyConfiguration(new PermissionConfiguration());
        builder.ApplyConfiguration(new RolePermissionConfiguration());
        builder.ApplyConfiguration(new ProjectUserAssignmentConfiguration());
        builder.ApplyConfiguration(new UserRoleProjectAssignmentConfiguration());
        builder.ApplyConfiguration(new UserPermissionAssignmentConfiguration());
        builder.ApplyConfiguration(new PermissionCacheVersionConfiguration());
    }
}
