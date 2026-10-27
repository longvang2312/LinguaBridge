using AppInHouse.Identity.Domain.Entities;

namespace AppInHouse.Identity.UnitTests.Domain;

public class EntityInvariantTests
{
    private static readonly Guid SomeId = Guid.NewGuid();

    // ---- Project ----

    [Fact]
    public void Project_Create_SetsDefaults()
    {
        var before = DateTime.UtcNow;

        var project = new Project("  ERP ", "ERP System", "  Internal ERP ");

        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.Equal("ERP", project.ProjectCode);
        Assert.Equal("ERP System", project.Name);
        Assert.Equal("Internal ERP", project.Description);
        Assert.True(project.IsActive);
        Assert.False(project.IsDeleted);
        Assert.Equal(DateTimeKind.Utc, project.CreatedAt.Kind);
        Assert.InRange(project.CreatedAt, before, DateTime.UtcNow);
        Assert.False(string.IsNullOrWhiteSpace(project.ConcurrencyToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Project_Create_RejectsBlankCode(string? code)
        => Assert.Throws<ArgumentException>(() => new Project(code!, "Name"));

    [Fact]
    public void Project_Create_RejectsTooLongValues()
    {
        Assert.Throws<ArgumentException>(() => new Project(new string('a', Project.ProjectCodeMaxLength + 1), "Name"));
        Assert.Throws<ArgumentException>(() => new Project("ERP", new string('a', Project.NameMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => new Project("ERP", "Name", new string('a', Project.DescriptionMaxLength + 1)));
        Assert.NotNull(new Project(new string('a', Project.ProjectCodeMaxLength), "Name"));
    }

    [Fact]
    public void Project_StateChanges_RotateConcurrencyToken()
    {
        var project = new Project("ERP", "ERP");
        var token = project.ConcurrencyToken;

        project.Deactivate();
        Assert.False(project.IsActive);
        Assert.NotEqual(token, project.ConcurrencyToken);

        token = project.ConcurrencyToken;
        project.Activate();
        Assert.True(project.IsActive);
        Assert.NotEqual(token, project.ConcurrencyToken);

        token = project.ConcurrencyToken;
        project.UpdateDetails("New name", null);
        Assert.Equal("New name", project.Name);
        Assert.Null(project.Description);
        Assert.NotEqual(token, project.ConcurrencyToken);
    }

    [Fact]
    public void Project_MarkDeleted_DeactivatesAndCannotBeReactivated()
    {
        var project = new Project("ERP", "ERP");

        project.MarkDeleted();

        Assert.True(project.IsDeleted);
        Assert.False(project.IsActive);
        Assert.Throws<InvalidOperationException>(project.Activate);
    }

    // ---- Permission ----

    [Fact]
    public void Permission_WithoutProject_IsGlobal()
    {
        var permission = new Permission("users.view", "View users", "Users");

        Assert.True(permission.IsGlobal);
        Assert.Null(permission.ProjectId);
        Assert.True(permission.IsActive);
        Assert.Equal(DateTimeKind.Utc, permission.CreatedAt.Kind);
    }

    [Fact]
    public void Permission_WithProject_IsNotGlobal()
    {
        var permission = new Permission("erp.orders.view", "View orders", "Orders", SomeId);

        Assert.False(permission.IsGlobal);
        Assert.Equal(SomeId, permission.ProjectId);
    }

    [Fact]
    public void Permission_Create_RejectsInvalidValues()
    {
        Assert.Throws<ArgumentException>(() => new Permission("", "Name", "Users"));
        Assert.Throws<ArgumentException>(() => new Permission("a", " ", "Users"));
        Assert.Throws<ArgumentException>(() => new Permission("a", "Name", ""));
        Assert.Throws<ArgumentException>(() => new Permission(new string('a', Permission.PermissionCodeMaxLength + 1), "Name", "Users"));
        Assert.Throws<ArgumentException>(() => new Permission("a", "Name", "Users", Guid.Empty));
    }

    [Fact]
    public void Permission_ActivateDeactivate_TogglesIsActive()
    {
        var permission = new Permission("users.view", "View users", "Users");

        permission.Deactivate();
        Assert.False(permission.IsActive);

        permission.Activate();
        Assert.True(permission.IsActive);
    }

    // ---- Assignments ----

    [Fact]
    public void RolePermission_Create_ValidatesArguments()
    {
        var rp = new RolePermission("role-1", SomeId);

        Assert.Equal("role-1", rp.RoleId);
        Assert.Equal(SomeId, rp.PermissionId);
        Assert.Equal(DateTimeKind.Utc, rp.GrantedAt.Kind);
        Assert.Throws<ArgumentException>(() => new RolePermission("", SomeId));
        Assert.Throws<ArgumentException>(() => new RolePermission("role-1", Guid.Empty));
    }

    [Fact]
    public void ProjectUserAssignment_Create_DefaultsAndToggles()
    {
        var assignment = new ProjectUserAssignment(SomeId, "user-1");

        Assert.False(assignment.IsProjectAdmin);
        Assert.False(assignment.IsBlocked);

        assignment.SetProjectAdmin(true);
        assignment.Block();
        Assert.True(assignment.IsProjectAdmin);
        Assert.True(assignment.IsBlocked);

        assignment.Unblock();
        Assert.False(assignment.IsBlocked);

        Assert.Throws<ArgumentException>(() => new ProjectUserAssignment(Guid.Empty, "user-1"));
        Assert.Throws<ArgumentException>(() => new ProjectUserAssignment(SomeId, " "));
    }

    [Fact]
    public void UserRoleProjectAssignment_Create_ValidatesArguments()
    {
        var assignment = new UserRoleProjectAssignment("user-1", "role-1", SomeId);

        Assert.Equal("user-1", assignment.UserId);
        Assert.Equal("role-1", assignment.RoleId);
        Assert.Equal(SomeId, assignment.ProjectId);
        Assert.Throws<ArgumentException>(() => new UserRoleProjectAssignment("", "role-1", SomeId));
        Assert.Throws<ArgumentException>(() => new UserRoleProjectAssignment("user-1", "", SomeId));
        Assert.Throws<ArgumentException>(() => new UserRoleProjectAssignment("user-1", "role-1", Guid.Empty));
    }

    [Fact]
    public void UserPermissionAssignment_Create_SupportsGrantAndRevoke()
    {
        var grant = new UserPermissionAssignment("user-1", SomeId, SomeId);
        var revoke = new UserPermissionAssignment("user-1", SomeId, SomeId, isRevoked: true);

        Assert.False(grant.IsRevoked);
        Assert.True(revoke.IsRevoked);
        Assert.Throws<ArgumentException>(() => new UserPermissionAssignment("", SomeId, SomeId));
        Assert.Throws<ArgumentException>(() => new UserPermissionAssignment("user-1", Guid.Empty, SomeId));
        Assert.Throws<ArgumentException>(() => new UserPermissionAssignment("user-1", SomeId, Guid.Empty));
    }

    // ---- Audit / versions ----

    [Fact]
    public void AuditLog_Create_SetsFieldsAndUtcTimestamp()
    {
        var log = new AuditLog("REVOKE", "Permission", "42", userId: "admin-1", projectId: SomeId,
            oldValuesJson: "{}", newValuesJson: "{\"a\":1}", ipAddress: "10.0.0.1");

        Assert.Equal("REVOKE", log.Action);
        Assert.Equal("Permission", log.EntityName);
        Assert.Equal("42", log.EntityId);
        Assert.Equal("admin-1", log.UserId);
        Assert.Equal(SomeId, log.ProjectId);
        Assert.Equal("10.0.0.1", log.IpAddress);
        Assert.Equal(DateTimeKind.Utc, log.Timestamp.Kind);
    }

    [Fact]
    public void AuditLog_Create_AllowsAnonymousActorAndRejectsInvalidValues()
    {
        var log = new AuditLog("LOGIN", "User", "u1");

        Assert.Null(log.UserId);
        Assert.Null(log.ProjectId);
        Assert.Throws<ArgumentException>(() => new AuditLog("", "User", "u1"));
        Assert.Throws<ArgumentException>(() => new AuditLog(new string('a', AuditLog.ActionMaxLength + 1), "User", "u1"));
        Assert.Throws<ArgumentException>(() => new AuditLog("LOGIN", "", "u1"));
        Assert.Throws<ArgumentException>(() => new AuditLog("LOGIN", "User", ""));
        Assert.Throws<ArgumentException>(() => new AuditLog("LOGIN", "User", "u1", ipAddress: new string('1', AuditLog.IpAddressMaxLength + 1)));
    }

    [Fact]
    public void PermissionCacheVersion_Invalidate_IncrementsVersion()
    {
        var version = new PermissionCacheVersion("User:abc");
        Assert.Equal(0, version.VersionNumber);

        var first = version.LastInvalidatedAt;
        version.Invalidate();
        version.Invalidate();

        Assert.Equal(2, version.VersionNumber);
        Assert.True(version.LastInvalidatedAt >= first);
        Assert.Throws<ArgumentException>(() => new PermissionCacheVersion(" "));
        Assert.Throws<ArgumentException>(() => new PermissionCacheVersion(new string('k', PermissionCacheVersion.ContextKeyMaxLength + 1)));
    }

    [Fact]
    public void ConfigurationVersion_Create_ValidatesArguments()
    {
        var version = new ConfigurationVersion(1, "Initial");

        Assert.Equal(1, version.VersionNumber);
        Assert.Equal("Initial", version.Description);
        Assert.Equal(DateTimeKind.Utc, version.AppliedAt.Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigurationVersion(0, "Initial"));
        Assert.Throws<ArgumentException>(() => new ConfigurationVersion(1, ""));
    }
}
