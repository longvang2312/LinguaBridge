namespace AppInHouse.Identity.Application.Admin.Audit;

/// <param name="OldValues">Snapshot before the change (null for creations). Must not contain secrets.</param>
/// <param name="NewValues">Snapshot after the change (null for deletions). Must not contain secrets.</param>
public sealed record AuditEntry(
    string Action,
    string EntityName,
    string EntityId,
    Guid? ProjectId = null,
    object? OldValues = null,
    object? NewValues = null);

public static class AuditActions
{
    public const string Create = "CREATE";
    public const string Update = "UPDATE";
    public const string StatusChange = "STATUS_CHANGE";
    public const string Assign = "ASSIGN";
    public const string SetRoles = "SET_ROLES";
    public const string SetPermissions = "SET_PERMISSIONS";
    public const string Lock = "LOCK";
    public const string Unlock = "UNLOCK";
    public const string ResetPassword = "RESET_PASSWORD";
    public const string RotateSecret = "ROTATE_SECRET";
}

public static class AuditEntities
{
    public const string Project = "Project";
    public const string ProjectUser = "ProjectUser";
    public const string UserRole = "UserRole";
    public const string UserPermission = "UserPermission";
    public const string Client = "Client";
    public const string User = "User";
    public const string Permission = "Permission";
    public const string Role = "Role";
}

/// <summary>
/// Writes an audit record for the current caller (actor and IP address are taken from the request).
/// Implementations redact password / secret fields, but callers must not pass secrets in the first place.
/// </summary>
public interface IAuditService
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
