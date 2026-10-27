using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>Immutable audit trail record of a sensitive operation.</summary>
public class AuditLog
{
    public const int UserIdMaxLength = 450;
    public const int ActionMaxLength = 50;
    public const int EntityNameMaxLength = 100;
    public const int EntityIdMaxLength = 100;
    public const int IpAddressMaxLength = 50;

    // Required by EF Core.
    private AuditLog() { }

    public AuditLog(
        string action,
        string entityName,
        string entityId,
        string? userId = null,
        Guid? projectId = null,
        string? oldValuesJson = null,
        string? newValuesJson = null,
        string? ipAddress = null)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("projectId must not be an empty Guid.", nameof(projectId));

        Id = Guid.NewGuid();
        Action = Guard.Required(action, ActionMaxLength, nameof(action));
        EntityName = Guard.Required(entityName, EntityNameMaxLength, nameof(entityName));
        EntityId = Guard.Required(entityId, EntityIdMaxLength, nameof(entityId));
        UserId = Guard.Optional(userId, UserIdMaxLength, nameof(userId));
        ProjectId = projectId;
        OldValuesJson = oldValuesJson;
        NewValuesJson = newValuesJson;
        IpAddress = Guard.Optional(ipAddress, IpAddressMaxLength, nameof(ipAddress));
        Timestamp = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public string? UserId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityName { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string? OldValuesJson { get; private set; }
    public string? NewValuesJson { get; private set; }
    public string? IpAddress { get; private set; }
    public DateTime Timestamp { get; private set; }
}
