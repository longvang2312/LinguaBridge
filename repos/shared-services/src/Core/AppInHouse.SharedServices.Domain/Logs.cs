namespace AppInHouse.SharedServices.Domain;

// Four separate logs, each for one reader: delivery (did the message go out?), media (what happened to a file?),
// audit (who changed what?) and operational (what went wrong inside the service?).

/// <summary>One attempt to send an email or a chat message.</summary>
public sealed class DeliveryLog
{
    private DeliveryLog()
    {
        ProjectCode = string.Empty;
        Recipient = string.Empty;
    }

    public Guid Id { get; private set; }
    public string ProjectCode { get; private set; }
    public ChannelType ChannelType { get; private set; }

    /// <summary>An email address or a chat id.</summary>
    public string Recipient { get; private set; }

    public DeliveryStatus Status { get; private set; }
    public string? ProviderResponse { get; private set; }
    public int? LatencyMs { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static DeliveryLog Create(
        string projectCode, ChannelType channelType, string recipient, DeliveryStatus status,
        string? providerResponse, int? latencyMs, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            ProjectCode = projectCode,
            ChannelType = channelType,
            Recipient = recipient,
            Status = status,
            ProviderResponse = providerResponse,
            LatencyMs = latencyMs,
            CreatedAt = now,
        };
}

/// <summary>An upload, download, thumbnail or delete of a stored file.</summary>
public sealed class MediaLog
{
    private MediaLog()
    {
        ProjectCode = string.Empty;
        Action = string.Empty;
        Status = string.Empty;
    }

    public Guid Id { get; private set; }
    public string ProjectCode { get; private set; }
    public Guid? FileId { get; private set; }

    /// <summary>Upload, Download, Thumbnail or Delete.</summary>
    public string Action { get; private set; }

    public string Status { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static MediaLog Create(string projectCode, Guid? fileId, string action, string status, string? errorMessage, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectCode = projectCode,
        FileId = fileId,
        Action = action,
        Status = status,
        ErrorMessage = errorMessage,
        CreatedAt = now,
    };
}

/// <summary>Who changed a configuration, pressed "test" or handled an API key. Details never contain a secret.</summary>
public sealed class AuditLog
{
    private AuditLog()
    {
        Actor = string.Empty;
        Action = string.Empty;
        ResourceType = string.Empty;
        ResourceId = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>A user's email, an API key's prefix, or "system".</summary>
    public string Actor { get; private set; }

    public string Action { get; private set; }
    public string ResourceType { get; private set; }
    public string ResourceId { get; private set; }
    public string? DetailsJson { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditLog Create(
        string actor, string action, string resourceType, string resourceId, string? detailsJson, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            Actor = actor,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            DetailsJson = detailsJson,
            CreatedAt = now,
        };
}

/// <summary>An unexpected error or a health event of the service itself.</summary>
public sealed class SystemLog
{
    private SystemLog()
    {
        Level = string.Empty;
        Category = string.Empty;
        Message = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Information, Warning or Error.</summary>
    public string Level { get; private set; }

    public string Category { get; private set; }
    public string Message { get; private set; }
    public string? Exception { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static SystemLog Create(string level, string category, string message, string? exception, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Level = level,
        Category = category,
        Message = message,
        Exception = exception,
        CreatedAt = now,
    };
}
