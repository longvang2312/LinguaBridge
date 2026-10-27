using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppInHouse.Identity.UnitTests.Persistence;

public class DbContextModelTests
{
    private static DbContextOptions<T> InMemory<T>() where T : DbContext
        => new DbContextOptionsBuilder<T>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static AppAuthDbContext NewAuthContext() => new(InMemory<AppAuthDbContext>());

    private static IEntityType Entity<T>(DbContext context)
        => context.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");

    private static IIndex? FindIndex(IEntityType entity, params string[] properties)
        => entity.GetIndexes().FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(properties));

    private static IForeignKey ForeignKey(IEntityType entity, string property, Type principal)
        => entity.GetForeignKeys().Single(fk =>
            fk.Properties.Select(p => p.Name).SequenceEqual(new[] { property }) && fk.PrincipalEntityType.ClrType == principal);

    // ---- Context composition ----

    [Fact]
    public void AuthContext_MapsAllAuthorizationTables()
    {
        using var context = NewAuthContext();

        var tables = new[]
        {
            Entity<Project>(context), Entity<Permission>(context), Entity<RolePermission>(context),
            Entity<ProjectUserAssignment>(context), Entity<UserRoleProjectAssignment>(context),
            Entity<UserPermissionAssignment>(context), Entity<PermissionCacheVersion>(context),
        }.Select(e => e.GetTableName()).ToArray();

        Assert.Equal(
            new[] { "Projects", "Permissions", "RolePermissions", "ProjectUserAssignments",
                    "UserRoleProjectAssignments", "UserPermissionAssignments", "PermissionCacheVersions" },
            tables);
        Assert.Null(context.Model.FindEntityType(typeof(AuditLog)));
    }

    [Fact]
    public void AuthContext_ReferencesIdentityTablesWithoutMigratingThem()
    {
        using var context = NewAuthContext();

        // Migration-related annotations are only available on the design-time model.
        var model = context.GetService<IDesignTimeModel>().Model;
        var users = model.FindEntityType(typeof(ApplicationUser))!;
        var roles = model.FindEntityType(typeof(ApplicationRole))!;

        Assert.Equal("AspNetUsers", users.GetTableName());
        Assert.Equal("AspNetRoles", roles.GetTableName());
        Assert.True(users.IsTableExcludedFromMigrations());
        Assert.True(roles.IsTableExcludedFromMigrations());
    }

    [Fact]
    public void AuditContext_MapsAuditTablesOnly()
    {
        using var context = new AppAuditDbContext(InMemory<AppAuditDbContext>());

        Assert.Equal("AuditLogs", Entity<AuditLog>(context).GetTableName());
        Assert.Equal("ConfigurationVersions", Entity<ConfigurationVersion>(context).GetTableName());
        Assert.Null(context.Model.FindEntityType(typeof(Project)));
    }

    [Fact]
    public void IdentityContext_UsesExtendedUserAndRole()
    {
        using var context = new AppIdentityDbContext(InMemory<AppIdentityDbContext>());

        var user = Entity<ApplicationUser>(context);
        var role = Entity<ApplicationRole>(context);

        Assert.Equal("AspNetUsers", user.GetTableName());
        Assert.Equal("AspNetRoles", role.GetTableName());
        Assert.Equal(ApplicationUser.FullNameMaxLength, user.FindProperty(nameof(ApplicationUser.FullName))!.GetMaxLength());
        Assert.False(user.FindProperty(nameof(ApplicationUser.FullName))!.IsNullable);
        Assert.True(user.FindProperty(nameof(ApplicationUser.LastLoginAt))!.IsNullable);
        Assert.Equal(ApplicationRole.DescriptionMaxLength, role.FindProperty(nameof(ApplicationRole.Description))!.GetMaxLength());
        Assert.Null(context.Model.FindEntityType(typeof(Project)));
    }

    // ---- Keys, lengths, nullability ----

    [Fact]
    public void Project_Configuration_HasKeyLengthsAndConcurrencyToken()
    {
        using var context = NewAuthContext();
        var entity = Entity<Project>(context);

        Assert.Equal(nameof(Project.Id), entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal(50, entity.FindProperty(nameof(Project.ProjectCode))!.GetMaxLength());
        Assert.Equal(100, entity.FindProperty(nameof(Project.Name))!.GetMaxLength());
        Assert.Equal(500, entity.FindProperty(nameof(Project.Description))!.GetMaxLength());
        Assert.True(entity.FindProperty(nameof(Project.Description))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(Project.ProjectCode))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(Project.ConcurrencyToken))!.IsConcurrencyToken);
    }

    [Fact]
    public void Permission_Configuration_HasLengthsAndOptionalProject()
    {
        using var context = NewAuthContext();
        var entity = Entity<Permission>(context);

        Assert.Equal(100, entity.FindProperty(nameof(Permission.PermissionCode))!.GetMaxLength());
        Assert.Equal(150, entity.FindProperty(nameof(Permission.DisplayName))!.GetMaxLength());
        Assert.Equal(50, entity.FindProperty(nameof(Permission.Module))!.GetMaxLength());
        Assert.True(entity.FindProperty(nameof(Permission.ProjectId))!.IsNullable);
        Assert.False(ForeignKey(entity, nameof(Permission.ProjectId), typeof(Project)).IsRequired);
    }

    [Fact]
    public void PermissionCacheVersion_Configuration_UsesContextKeyAsKey()
    {
        using var context = NewAuthContext();
        var entity = Entity<PermissionCacheVersion>(context);

        var key = entity.FindPrimaryKey()!.Properties.Single();
        Assert.Equal(nameof(PermissionCacheVersion.ContextKey), key.Name);
        Assert.Equal(150, key.GetMaxLength());
        Assert.Equal(typeof(long), entity.FindProperty(nameof(PermissionCacheVersion.VersionNumber))!.ClrType);
    }

    [Fact]
    public void AuditLog_Configuration_HasLengthsAndNullableActor()
    {
        using var context = new AppAuditDbContext(InMemory<AppAuditDbContext>());
        var entity = Entity<AuditLog>(context);

        Assert.Equal(50, entity.FindProperty(nameof(AuditLog.Action))!.GetMaxLength());
        Assert.Equal(100, entity.FindProperty(nameof(AuditLog.EntityName))!.GetMaxLength());
        Assert.Equal(100, entity.FindProperty(nameof(AuditLog.EntityId))!.GetMaxLength());
        Assert.Equal(50, entity.FindProperty(nameof(AuditLog.IpAddress))!.GetMaxLength());
        Assert.True(entity.FindProperty(nameof(AuditLog.UserId))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(AuditLog.OldValuesJson))!.IsNullable);
        Assert.Empty(entity.GetForeignKeys());
    }

    // ---- Indexes ----

    [Fact]
    public void Indexes_UniqueConstraints_AreConfigured()
    {
        using var context = NewAuthContext();

        Assert.True(FindIndex(Entity<Project>(context), nameof(Project.ProjectCode))?.IsUnique);
        Assert.True(FindIndex(Entity<Permission>(context), nameof(Permission.PermissionCode))?.IsUnique);
        Assert.True(FindIndex(Entity<ProjectUserAssignment>(context),
            nameof(ProjectUserAssignment.ProjectId), nameof(ProjectUserAssignment.UserId))?.IsUnique);
        Assert.True(FindIndex(Entity<UserRoleProjectAssignment>(context),
            nameof(UserRoleProjectAssignment.UserId), nameof(UserRoleProjectAssignment.RoleId),
            nameof(UserRoleProjectAssignment.ProjectId))?.IsUnique);
        Assert.True(FindIndex(Entity<RolePermission>(context),
            nameof(RolePermission.RoleId), nameof(RolePermission.PermissionId))?.IsUnique);
        Assert.True(FindIndex(Entity<UserPermissionAssignment>(context),
            nameof(UserPermissionAssignment.UserId), nameof(UserPermissionAssignment.PermissionId),
            nameof(UserPermissionAssignment.ProjectId))?.IsUnique);
    }

    [Fact]
    public void Indexes_QueryOptimization_AreConfigured()
    {
        using var auth = NewAuthContext();
        using var audit = new AppAuditDbContext(InMemory<AppAuditDbContext>());

        var userProject = FindIndex(Entity<UserRoleProjectAssignment>(auth),
            nameof(UserRoleProjectAssignment.UserId), nameof(UserRoleProjectAssignment.ProjectId));
        Assert.NotNull(userProject);
        Assert.False(userProject.IsUnique);

        Assert.NotNull(FindIndex(Entity<AuditLog>(audit), nameof(AuditLog.Timestamp)));
        Assert.NotNull(FindIndex(Entity<AuditLog>(audit), nameof(AuditLog.UserId)));
    }

    // ---- Foreign keys ----

    [Fact]
    public void ForeignKeys_ReferenceProjectsPermissionsUsersAndRoles()
    {
        using var context = NewAuthContext();

        var rolePermission = Entity<RolePermission>(context);
        Assert.NotNull(ForeignKey(rolePermission, nameof(RolePermission.RoleId), typeof(ApplicationRole)));
        Assert.NotNull(ForeignKey(rolePermission, nameof(RolePermission.PermissionId), typeof(Permission)));

        var projectUser = Entity<ProjectUserAssignment>(context);
        Assert.NotNull(ForeignKey(projectUser, nameof(ProjectUserAssignment.ProjectId), typeof(Project)));
        Assert.NotNull(ForeignKey(projectUser, nameof(ProjectUserAssignment.UserId), typeof(ApplicationUser)));

        var userRoleProject = Entity<UserRoleProjectAssignment>(context);
        Assert.NotNull(ForeignKey(userRoleProject, nameof(UserRoleProjectAssignment.UserId), typeof(ApplicationUser)));
        Assert.NotNull(ForeignKey(userRoleProject, nameof(UserRoleProjectAssignment.RoleId), typeof(ApplicationRole)));
        Assert.NotNull(ForeignKey(userRoleProject, nameof(UserRoleProjectAssignment.ProjectId), typeof(Project)));

        var userPermission = Entity<UserPermissionAssignment>(context);
        Assert.NotNull(ForeignKey(userPermission, nameof(UserPermissionAssignment.UserId), typeof(ApplicationUser)));
        Assert.NotNull(ForeignKey(userPermission, nameof(UserPermissionAssignment.PermissionId), typeof(Permission)));
        Assert.NotNull(ForeignKey(userPermission, nameof(UserPermissionAssignment.ProjectId), typeof(Project)));
    }

    [Fact]
    public void ForeignKeys_DoNotCascadeFromProjectOrPermission()
    {
        // Avoids multiple cascade paths on SQL Server; projects are soft-deleted anyway.
        using var context = NewAuthContext();

        var restricted = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Project) || fk.PrincipalEntityType.ClrType == typeof(Permission));

        Assert.All(restricted, fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
    }

    [Fact]
    public void ForeignKey_PropertyLengths_MatchIdentityKeyLength()
    {
        using var context = NewAuthContext();

        Assert.Equal(450, Entity<RolePermission>(context).FindProperty(nameof(RolePermission.RoleId))!.GetMaxLength());
        Assert.Equal(450, Entity<ProjectUserAssignment>(context).FindProperty(nameof(ProjectUserAssignment.UserId))!.GetMaxLength());
        Assert.Equal(450, Entity<UserRoleProjectAssignment>(context).FindProperty(nameof(UserRoleProjectAssignment.RoleId))!.GetMaxLength());
        Assert.Equal(450, Entity<UserPermissionAssignment>(context).FindProperty(nameof(UserPermissionAssignment.UserId))!.GetMaxLength());
    }

    // ---- Round trips through the in-memory provider ----

    [Fact]
    public async Task AuthContext_RoundTrip_LoadsNavigationProperties()
    {
        var options = InMemory<AppAuthDbContext>();
        var project = new Project("ERP", "ERP System");
        var permission = new Permission("orders.view", "View orders", "Orders", project.Id);
        var rolePermission = new RolePermission("role-1", permission.Id);
        var projectUser = new ProjectUserAssignment(project.Id, "user-1", isProjectAdmin: true);
        var userRole = new UserRoleProjectAssignment("user-1", "role-1", project.Id);
        var userPermission = new UserPermissionAssignment("user-1", permission.Id, project.Id, isRevoked: true);

        await using (var seed = new AppAuthDbContext(options))
        {
            seed.AddRange(project, permission, rolePermission, projectUser, userRole, userPermission);
            await seed.SaveChangesAsync();
        }

        await using var context = new AppAuthDbContext(options);

        var loadedPermission = await context.Permissions.Include(p => p.Project).SingleAsync();
        Assert.Equal("ERP", loadedPermission.Project!.ProjectCode);
        Assert.False(loadedPermission.IsGlobal);

        var loadedRolePermission = await context.RolePermissions.Include(rp => rp.Permission).SingleAsync();
        Assert.Equal("orders.view", loadedRolePermission.Permission.PermissionCode);

        var loadedProjectUser = await context.ProjectUserAssignments.Include(a => a.Project).SingleAsync();
        Assert.Equal(project.Id, loadedProjectUser.Project.Id);
        Assert.True(loadedProjectUser.IsProjectAdmin);

        var loadedUserRole = await context.UserRoleProjectAssignments.Include(a => a.Project).SingleAsync();
        Assert.Equal("ERP", loadedUserRole.Project.ProjectCode);

        var loadedUserPermission = await context.UserPermissionAssignments
            .Include(a => a.Permission).Include(a => a.Project).SingleAsync();
        Assert.True(loadedUserPermission.IsRevoked);
        Assert.Equal("orders.view", loadedUserPermission.Permission.PermissionCode);
        Assert.Equal("ERP", loadedUserPermission.Project.ProjectCode);
    }

    [Fact]
    public async Task AuthContext_GlobalPermission_PersistsWithoutProject()
    {
        var options = InMemory<AppAuthDbContext>();

        await using (var seed = new AppAuthDbContext(options))
        {
            seed.Permissions.Add(new Permission("users.view", "View users", "Users"));
            await seed.SaveChangesAsync();
        }

        await using var context = new AppAuthDbContext(options);
        var loaded = await context.Permissions.SingleAsync();

        Assert.True(loaded.IsGlobal);
        Assert.Null(loaded.ProjectId);
        Assert.Null(loaded.Project);
    }

    [Fact]
    public async Task AuthContext_PermissionCacheVersion_PersistsIncrement()
    {
        var options = InMemory<AppAuthDbContext>();
        var version = new PermissionCacheVersion("Project:ERP");

        await using (var seed = new AppAuthDbContext(options))
        {
            seed.PermissionCacheVersions.Add(version);
            await seed.SaveChangesAsync();
        }

        await using (var update = new AppAuthDbContext(options))
        {
            var tracked = await update.PermissionCacheVersions.FindAsync("Project:ERP");
            tracked!.Invalidate();
            await update.SaveChangesAsync();
        }

        await using var context = new AppAuthDbContext(options);
        Assert.Equal(1, (await context.PermissionCacheVersions.SingleAsync()).VersionNumber);
    }

    [Fact]
    public async Task AuditContext_RoundTrip_PersistsEntries()
    {
        var options = InMemory<AppAuditDbContext>();

        await using (var seed = new AppAuditDbContext(options))
        {
            seed.AuditLogs.Add(new AuditLog("CREATE", "Role", "r1", userId: "admin", newValuesJson: "{}"));
            seed.ConfigurationVersions.Add(new ConfigurationVersion(1, "Initial"));
            await seed.SaveChangesAsync();
        }

        await using var context = new AppAuditDbContext(options);
        var log = await context.AuditLogs.SingleAsync();
        Assert.Equal("CREATE", log.Action);
        Assert.Equal("admin", log.UserId);
        Assert.Equal(1, (await context.ConfigurationVersions.SingleAsync()).VersionNumber);
    }
}
