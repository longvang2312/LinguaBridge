using System.Text.Json;

namespace AppInHouse.SharedServices.ClientSdk;

/// <summary>
/// A channel configuration as the API returns it. The secret is never part of an answer: <see cref="HasSecret"/> says whether one is
/// stored and <see cref="MaskedSecret"/> is the placeholder to show in its place.
/// </summary>
public sealed record ChannelConfigurationResponse(
    Guid Id,
    string ProjectCode,
    string ChannelType,
    string ProviderType,
    string Name,
    int Version,
    string Status,
    JsonElement Settings,
    bool HasSecret,
    string MaskedSecret,
    string LastTestStatus,
    DateTimeOffset? LastTestedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Creates version 1 of a configuration, or the next version of an existing one (project, channel and name identify it).
/// <see cref="Secret"/> is write-only: leave it null to keep the stored secret.
/// </summary>
public sealed record SaveChannelConfigurationRequest(
    string ProjectCode, string ChannelType, string ProviderType, string Name, JsonElement? Settings = null, string? Secret = null);

public sealed record RollbackConfigurationRequest(string ProjectCode, string ChannelType, string Name, int? TargetVersion = null);

/// <summary>
/// Send one email. Give a <see cref="Template"/> (<c>EmailVerification</c>, <c>PasswordReset</c>, <c>SystemNotification</c>) with its
/// <see cref="Variables"/>, or your own <see cref="Subject"/> and body (<c>{{variables}}</c> in them are replaced too). The project
/// defaults to the project of the API key.
/// </summary>
public sealed record SendEmailRequest(
    string To, string? Template = null, Dictionary<string, string>? Variables = null,
    string? Subject = null, string? HtmlBody = null, string? TextBody = null, string? ProjectCode = null);

public sealed record SendEmailResponse(
    bool Success, string Status, Guid? DeliveryLogId, int LatencyMs, string? ConfigurationProject, int? ConfigurationVersion);

/// <summary>
/// Test an email configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated.
/// Without it, <see cref="ProviderType"/>, <see cref="Settings"/> and <see cref="Secret"/> are tried from memory: nothing is saved or
/// activated. <see cref="Secret"/> is write-only.
/// </summary>
public sealed record SendTestEmailRequest(
    string TestRecipient, Guid? ConfigurationId = null, string? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

/// <summary>The outcome of a test. A failed test is an answer (<see cref="Success"/> false with a short, safe <see cref="Message"/>), not an exception.</summary>
public sealed record SendTestEmailResponse(
    bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

/// <summary>One delivery attempt: who it was for, whether it went out, the provider's short answer and the time. Never the subject, body or a credential.</summary>
public sealed record DeliveryLogDto(
    Guid Id, string ProjectCode, string ChannelType, string Recipient, string Status, string? ProviderResponse, int? LatencyMs, DateTimeOffset CreatedAt);

/// <summary>
/// Send one chat message (Telegram, Discord or Slack, whichever the project's active chat configuration names). <see cref="Recipient"/> is a
/// chat id for Telegram, an optional channel for Slack, unused by Discord; empty means the configuration's default. <see cref="Format"/>
/// (<c>PlainText</c>, <c>Markdown</c>, <c>MarkdownV2</c>) left out means the configuration's default; <c>PlainText</c> is always shown as
/// written. The project defaults to the project of the API key.
/// </summary>
public sealed record SendChatMessageRequest(
    string Text, string? Recipient = null, string? Format = null, string? ProjectCode = null, Dictionary<string, string>? Metadata = null);

public sealed record SendChatMessageResponse(
    bool Success, string Status, Guid? DeliveryLogId, int LatencyMs, string? ConfigurationProject, int? ConfigurationVersion);

/// <summary>
/// Test a chat configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated. Without
/// it, <see cref="ProviderType"/>, <see cref="Settings"/> and <see cref="Secret"/> (a bot token or a webhook address) are tried from memory:
/// nothing is saved or activated. <see cref="Secret"/> is write-only. <see cref="Recipient"/> and <see cref="Message"/> are optional.
/// </summary>
public sealed record SendTestChatMessageRequest(
    string? Recipient = null, string? Message = null, Guid? ConfigurationId = null,
    string? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

/// <summary>The outcome of a chat test. A failed test is an answer (<see cref="Success"/> false with a short, safe <see cref="Message"/>), not an exception.</summary>
public sealed record SendTestChatMessageResponse(
    bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

/// <summary>What the service knows about a stored file (never where it is kept). <see cref="Url"/> and <see cref="ThumbnailUrl"/> are relative to the API address.</summary>
public sealed record StoredFileDto(
    Guid Id, string ProjectCode, string StorageProvider, string OriginalFileName, string MimeType, long FileSizeBytes, string FileHashSha256,
    bool IsPublic, bool HasThumbnail, DateTimeOffset CreatedAt, string Url, string? ThumbnailUrl);

/// <summary>
/// Upload one file. <see cref="ContentType"/> is only what the caller claims: the service reads the real type from the bytes and refuses a
/// file that is not what it claims to be. The project defaults to the project of the API key.
/// </summary>
public sealed record UploadFileRequest(string FileName, string? ContentType = null, bool IsPublic = false, string? ProjectCode = null);

public sealed record UploadFileResponse(StoredFileDto File, string Url, string? ThumbnailUrl, string? PublicUrl);

/// <summary>A file's content, streamed. Dispose it to release the connection.</summary>
public sealed class FileStreamResponse(Stream content, string contentType, string fileName, long? length, string? eTag, IDisposable? owner = null) : IAsyncDisposable, IDisposable
{
    public Stream Content { get; } = content;
    public string ContentType { get; } = contentType;
    public string FileName { get; } = fileName;
    public long? Length { get; } = length;
    public string? ETag { get; } = eTag;

    public void Dispose()
    {
        Content.Dispose();
        owner?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        owner?.Dispose();
    }
}

public sealed record MediaLogDto(Guid Id, string ProjectCode, Guid? FileId, string Action, string Status, string? ErrorMessage, DateTimeOffset CreatedAt);

/// <summary>
/// Test a storage configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated. Without
/// it, <see cref="ProviderType"/>, <see cref="Settings"/> and <see cref="Secret"/> (the S3 secret access key) are tried from memory: nothing
/// is saved or activated. <see cref="Secret"/> is write-only.
/// </summary>
public sealed record StorageTestRequest(Guid? ConfigurationId = null, string? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

/// <summary>The outcome of a storage test. A failed test is an answer (<see cref="Success"/> false with a short, safe <see cref="Message"/>), not an exception.</summary>
public sealed record StorageTestResponse(bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

/// <summary>A key as the service shows it: what it is for and its state (<c>Active</c>, <c>Expired</c>, <c>Revoked</c>), never the key itself.</summary>
public sealed record ApiKeyDto(
    Guid Id, string ProjectCode, string Name, string KeyPrefix, IReadOnlyList<string> Scopes, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt);

/// <summary>Make a key. <see cref="ExpiresInDays"/> left out means it does not expire (1 to 3650 days otherwise).</summary>
public sealed record CreateApiKeyRequest(string ProjectCode, string Name, string[] Scopes, int? ExpiresInDays = null);

/// <summary>The answer to creating a key. <see cref="ApiKey"/> is the full key, shown this one time only: keep it in a secret store.</summary>
public sealed record CreateApiKeyResponse(ApiKeyDto Key, string ApiKey)
{
    public override string ToString() => $"CreateApiKeyResponse {Key.KeyPrefix}...";
}

public sealed record RotateApiKeyResponse(ApiKeyDto Key, string ApiKey, ApiKeyDto OldKey)
{
    public override string ToString() => $"RotateApiKeyResponse {Key.KeyPrefix}...";
}

/// <summary>One audit entry. <see cref="DetailsJson"/> has been cleaned of anything that looks like a secret.</summary>
public sealed record AuditLogDto(Guid Id, string Actor, string Action, string ResourceType, string ResourceId, string? DetailsJson, DateTimeOffset CreatedAt);

// ------------------------------------------------------------------------------------------------ notifications

/// <summary>
/// Send a notification built from a template. <see cref="Data"/> fills the <c>{{placeholders}}</c> (text, numbers, true/false). With
/// <see cref="Queue"/> the messages are queued and retried by the service instead of being sent now.
/// </summary>
public sealed record DispatchNotificationRequest(
    string TemplateCode, IReadOnlyDictionary<string, string>? Data = null, string? RecipientEmail = null, string? RecipientChatId = null,
    string? ProjectCode = null, string? Priority = null, bool? Queue = null);

public sealed record NotificationChannelResult(string Channel, bool Success, string? Error, Guid? DeliveryLogId, bool IsFallback);

/// <summary>Status: Sent, Partial (one of two channels delivered), Fallback (the other channel delivered a summary), or Queued.</summary>
public sealed record DispatchNotificationResponse(
    bool Success, string Status, IReadOnlyList<NotificationChannelResult> Channels, int LatencyMs, Guid LogId, IReadOnlyList<Guid> JobIds);

public sealed record NotificationTemplateDto(
    string Code, string Name, string SubjectTemplate, string BodyTemplate, string DefaultChannel, string Priority, bool IsActive,
    IReadOnlyList<string> Placeholders);

public sealed record NotificationLogDto(
    Guid Id, string ProjectCode, string TemplateCode, string Channels, string Status, string? Recipient, int LatencyMs, DateTimeOffset CreatedAt);

// ------------------------------------------------------------------------------------------------ jobs

public sealed record JobQueueStatusDto(string Name, string Purpose, int Pending, int Processing, int CompletedLast24h, int Failed, string Status);

public sealed record RetryFailedResponse(string Queue, int Requeued);

public sealed record ScheduledTaskDto(
    string Id, string Name, string Description, string Cron, DateTimeOffset? LastRunAt, DateTimeOffset NextRunAt,
    string? LastStatus, string? LastError, int Runs, bool IsRunning);

public sealed record QueueThumbnailResponse(Guid JobId);

// ------------------------------------------------------------------------------------------------ webhooks

public sealed record WebhookSubscriptionDto(
    Guid Id, string ProjectCode, string Name, string TargetUrl, string SecretMask, IReadOnlyList<string> Events, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset? LastDeliveryAt, string? LastStatus, int SuccessCount, int FailureCount, int? SuccessRatePercent);

/// <summary>Subscribe an address to events (<c>email.sent</c>, <c>email.failed</c>, <c>chat.sent</c>, <c>chat.failed</c>, <c>file.uploaded</c>, <c>webhook.received</c> or <c>*</c>). The key is generated when left out.</summary>
public sealed record CreateWebhookSubscriptionRequest(string Name, string TargetUrl, IReadOnlyList<string> Events, string? SecretKey = null, string? ProjectCode = null);

/// <summary>The subscription and its signing key, shown this one time only. Each post carries <c>X-AIH-Signature: sha256={hex HMAC-SHA256 of the body}</c>.</summary>
public sealed record CreateWebhookSubscriptionResponse(WebhookSubscriptionDto Subscription, string SecretKey)
{
    public override string ToString() => $"CreateWebhookSubscriptionResponse {Subscription.Id}";
}

public sealed record WebhookTestResult(bool Success, int? StatusCode, string? Error, int LatencyMs);

public sealed record InboundSourceDto(
    string Source, string Path, string Verification, string Description, bool Configured, int Received24h, int Rejected24h, DateTimeOffset? LastReceivedAt);

// ------------------------------------------------------------------------------------------------ projects

public sealed record ProjectTenantDto(
    string ProjectCode, string Name, string Description, string Environment, int? DailyEmailQuota, int? DailyChatQuota,
    long? StorageQuotaBytes, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>Register a project. A quota left out means no limit; <see cref="Environment"/> is <c>Staging</c> or <c>Production</c>.</summary>
public sealed record CreateProjectRequest(
    string ProjectCode, string Name, string? Description = null, string? Environment = null,
    int? DailyEmailQuota = null, int? DailyChatQuota = null, long? StorageQuotaBytes = null);

/// <summary>Replaces the project's description, environment, quotas and state: send every field.</summary>
public sealed record UpdateProjectRequest(
    string Name, string? Description = null, string? Environment = null,
    int? DailyEmailQuota = null, int? DailyChatQuota = null, long? StorageQuotaBytes = null, bool? IsActive = null);

public sealed record ProjectUsageDto(
    string ProjectCode, DateOnly Date, int EmailsSentToday, int? DailyEmailQuota, int ChatSentToday, int? DailyChatQuota,
    long StorageBytes, long? StorageQuotaBytes, int FileCount, int ActiveApiKeys);

// ------------------------------------------------------------------------------------------------ metrics

public sealed record ServiceHealthDto(string Id, string Name, string Status, string Detail, IReadOnlyDictionary<string, string> Metrics);

public sealed record HealthStatusDto(string Overall, DateTimeOffset GeneratedAt, IReadOnlyList<ServiceHealthDto> Services);

public sealed record SystemMetricsOverviewDto(
    DateTimeOffset GeneratedAt, DateTimeOffset StartedAt, long UptimeSeconds,
    int ServicesTotal, int ServicesHealthy, int ServicesDegraded, int ServicesIdle,
    long TotalRequests24h, int DeliveriesSent24h, int DeliveriesFailed24h, double? SuccessRatePercent,
    long StorageBytes, int FileCount, int ActiveApiKeys, int ActiveProjects, int PendingJobs, int FailedJobs,
    IReadOnlyList<ServiceHealthDto> Services);

/// <summary>The API refused or failed a call. <see cref="Errors"/> holds field-level messages for a 400.</summary>
public sealed class SharedServicesApiException(int statusCode, string message, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}
