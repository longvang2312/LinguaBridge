using System.Text.RegularExpressions;

namespace AppInHouse.SharedServices.Domain;

// The platform services that sit on top of the channels (email, chat, storage): notifications, background jobs, webhooks and projects.

public enum NotificationChannel
{
    Email,
    Chat,

    /// <summary>Email and chat together, sent in parallel.</summary>
    Both,
}

public enum NotificationPriority
{
    Low,
    Normal,

    /// <summary>High and Critical notifications fall back to the other channel when the one they were meant for fails.</summary>
    High,
    Critical,
}

public enum ProjectEnvironment
{
    Staging,
    Production,
}

public enum JobStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
}

public enum InboundWebhookStatus
{
    /// <summary>Accepted and waiting in the queue.</summary>
    Received,
    Processed,

    /// <summary>Refused: unknown signature, no secret configured, wrong content.</summary>
    Rejected,
    Failed,
}

/// <summary>The names of the queues of the job engine.</summary>
public static class JobQueues
{
    public const string Email = "email";
    public const string Chat = "chat";
    public const string MediaThumbnail = "media-thumbnail";
    public const string Outbox = "outbox";
    public const string Webhook = "webhook";

    public static IReadOnlyList<string> All { get; } = [Email, Chat, MediaThumbnail, Outbox, Webhook];

    public static bool IsKnown(string? queue) => queue is not null && All.Contains(queue, StringComparer.Ordinal);
}

/// <summary>The events a webhook subscription can ask for.</summary>
public static class WebhookEvents
{
    public const string EmailSent = "email.sent";
    public const string EmailFailed = "email.failed";
    public const string ChatSent = "chat.sent";
    public const string ChatFailed = "chat.failed";
    public const string FileUploaded = "file.uploaded";
    public const string WebhookReceived = "webhook.received";

    /// <summary>Delivered only by the "test" action of a subscription; nobody subscribes to it.</summary>
    public const string Test = "webhook.test";

    /// <summary>Subscribes to every event.</summary>
    public const string All = "*";

    public static IReadOnlyList<string> Known { get; } = [EmailSent, EmailFailed, ChatSent, ChatFailed, FileUploaded, WebhookReceived];

    public static bool IsKnown(string name) => name == All || Known.Contains(name, StringComparer.Ordinal);
}

/// <summary>A message with placeholders such as <c>{{UserName}}</c>, sent by the notification service to email, chat or both.</summary>
public sealed partial class NotificationTemplate
{
    public const int MaxSubjectLength = 200;
    public const int MaxBodyLength = 20_000;

    private NotificationTemplate()
    {
        Code = string.Empty;
        Name = string.Empty;
        SubjectTemplate = string.Empty;
        BodyTemplate = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>The name callers use, for example <c>PasswordReset</c>. Unique.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }
    public string SubjectTemplate { get; private set; }

    /// <summary>Plain text with placeholders. In an email it becomes the text part and (escaped) the HTML part; in chat it is the message.</summary>
    public string BodyTemplate { get; private set; }

    public NotificationChannel DefaultChannel { get; private set; }
    public NotificationPriority Priority { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,59}$")]
    private static partial Regex CodePattern();

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    public static NotificationTemplate Create(
        string code, string name, string subjectTemplate, string bodyTemplate, NotificationChannel channel, NotificationPriority priority, DateTimeOffset now)
    {
        if (!IsValidCode(code)) throw new DomainException("A template code is 2 to 60 letters, digits or '_', starting with a letter.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A template needs a name.");
        if (string.IsNullOrWhiteSpace(subjectTemplate) || subjectTemplate.Length > MaxSubjectLength) throw new DomainException($"The subject is required and at most {MaxSubjectLength} characters.");
        if (string.IsNullOrWhiteSpace(bodyTemplate) || bodyTemplate.Length > MaxBodyLength) throw new DomainException($"The body is required and at most {MaxBodyLength} characters.");
        if (!Enum.IsDefined(channel)) throw new DomainException("Unknown channel.");
        if (!Enum.IsDefined(priority)) throw new DomainException("Unknown priority.");

        return new NotificationTemplate
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = name.Trim(),
            SubjectTemplate = subjectTemplate,
            BodyTemplate = bodyTemplate,
            DefaultChannel = channel,
            Priority = priority,
            IsActive = true,
            CreatedAt = now,
        };
    }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>What happened to one dispatched notification.</summary>
public sealed class NotificationLog
{
    private NotificationLog()
    {
        ProjectCode = string.Empty;
        TemplateCode = string.Empty;
        Channels = string.Empty;
        Status = string.Empty;
    }

    public Guid Id { get; private set; }
    public string ProjectCode { get; private set; }
    public string TemplateCode { get; private set; }

    /// <summary>What was used, for example <c>Email</c>, <c>Email+Chat</c> or <c>Email→Chat (fallback)</c>.</summary>
    public string Channels { get; private set; }

    /// <summary>Sent, Fallback, Partial, Failed or Queued.</summary>
    public string Status { get; private set; }

    public string? Recipient { get; private set; }
    public int LatencyMs { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static NotificationLog Create(
        string projectCode, string templateCode, string channels, string status, string? recipient, int latencyMs, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            ProjectCode = projectCode,
            TemplateCode = templateCode,
            Channels = channels,
            Status = status,
            Recipient = recipient is { Length: > 200 } ? recipient[..200] : recipient,
            LatencyMs = latencyMs,
            CreatedAt = now,
        };
}

/// <summary>An application that uses the platform: its identity, its limits for one day and whether it may use the services.</summary>
public sealed class ProjectTenant
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;

    private ProjectTenant()
    {
        ProjectCode = string.Empty;
        Name = string.Empty;
        Description = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Lower case, as in every other table; unique.</summary>
    public string ProjectCode { get; private set; }

    public string Name { get; private set; }
    public string Description { get; private set; }
    public ProjectEnvironment Environment { get; private set; }

    /// <summary>Emails per UTC day; null means no limit.</summary>
    public int? DailyEmailQuota { get; private set; }

    /// <summary>Chat messages per UTC day; null means no limit.</summary>
    public int? DailyChatQuota { get; private set; }

    /// <summary>Bytes of stored files; null means no limit.</summary>
    public long? StorageQuotaBytes { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The project of the platform itself; it must stay active.</summary>
    public const string PlatformCode = "default";

    public static ProjectTenant Create(
        string projectCode, string name, string? description, ProjectEnvironment environment,
        int? dailyEmailQuota, int? dailyChatQuota, long? storageQuotaBytes, DateTimeOffset now)
    {
        var tenant = new ProjectTenant { Id = Guid.NewGuid(), ProjectCode = projectCode, IsActive = true, CreatedAt = now };
        tenant.Update(name, description, environment, dailyEmailQuota, dailyChatQuota, storageQuotaBytes, true);
        return tenant;
    }

    public void Update(
        string name, string? description, ProjectEnvironment environment,
        int? dailyEmailQuota, int? dailyChatQuota, long? storageQuotaBytes, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength) throw new DomainException($"The name is required and at most {MaxNameLength} characters.");
        if ((description?.Length ?? 0) > MaxDescriptionLength) throw new DomainException($"The description is at most {MaxDescriptionLength} characters.");
        if (!Enum.IsDefined(environment)) throw new DomainException("Unknown environment.");
        if (dailyEmailQuota is < 0 || dailyChatQuota is < 0 || storageQuotaBytes is < 0) throw new DomainException("A quota cannot be negative (leave it empty for no limit).");
        if (!isActive && ProjectCode == PlatformCode) throw new DomainException("The platform project cannot be deactivated.");

        Name = name.Trim();
        Description = description?.Trim() ?? string.Empty;
        Environment = environment;
        DailyEmailQuota = dailyEmailQuota;
        DailyChatQuota = dailyChatQuota;
        StorageQuotaBytes = storageQuotaBytes;
        IsActive = isActive;
    }
}

/// <summary>An application's request to be told about events: where to post, how to sign, which events.</summary>
public sealed class WebhookSubscription
{
    public const int MaxNameLength = 100;
    public const int MaxUrlLength = 500;
    public const int MinSecretLength = 16;

    private WebhookSubscription()
    {
        ProjectCode = string.Empty;
        Name = string.Empty;
        TargetUrl = string.Empty;
        ProtectedSecret = string.Empty;
        SecretHint = string.Empty;
        SubscribedEvents = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>The project that owns it; it receives that project's events (the platform's subscriptions receive every project's).</summary>
    public string ProjectCode { get; private set; }

    public string Name { get; private set; }
    public string TargetUrl { get; private set; }

    /// <summary>The signing key, protected by the data protection key ring (it is needed in clear text to sign, so it cannot be a hash).</summary>
    public string ProtectedSecret { get; private set; }

    /// <summary>The last four characters of the key, so a list can show which one it is.</summary>
    public string SecretHint { get; private set; }

    /// <summary>Comma-separated event names (or <c>*</c>).</summary>
    public string SubscribedEvents { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastDeliveryAt { get; private set; }

    /// <summary>The last answer: "200", "503", or a short error.</summary>
    public string? LastStatus { get; private set; }

    public int SuccessCount { get; private set; }
    public int FailureCount { get; private set; }

    public IReadOnlyList<string> EventList => SubscribedEvents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Wants(string eventName) => EventList.Contains(WebhookEvents.All) || EventList.Contains(eventName, StringComparer.Ordinal);

    public static WebhookSubscription Create(
        string projectCode, string name, string targetUrl, string protectedSecret, string plainSecret, IEnumerable<string> events, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength) throw new DomainException($"The name is required and at most {MaxNameLength} characters.");
        if (targetUrl.Length > MaxUrlLength || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new DomainException("The target must be an http or https address without a user name or password.");
        if (plainSecret.Length < MinSecretLength) throw new DomainException($"The secret key is at least {MinSecretLength} characters.");

        var list = events.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) throw new DomainException("Subscribe to at least one event.");
        var unknown = list.Where(e => !WebhookEvents.IsKnown(e)).ToList();
        if (unknown.Count > 0) throw new DomainException($"Unknown event: {string.Join(", ", unknown)}.");

        return new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            ProjectCode = projectCode,
            Name = name.Trim(),
            TargetUrl = targetUrl,
            ProtectedSecret = protectedSecret,
            SecretHint = plainSecret[^4..],
            SubscribedEvents = string.Join(',', list),
            IsActive = true,
            CreatedAt = now,
        };
    }

    public void RecordDelivery(bool success, string status, DateTimeOffset now)
    {
        LastDeliveryAt = now;
        LastStatus = status.Length > 100 ? status[..100] : status;
        if (success) SuccessCount++;
        else FailureCount++;
    }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>A call that arrived at <c>/api/v1/webhooks/incoming/{source}</c>.</summary>
public sealed class InboundWebhookLog
{
    public const int MaxStoredPayloadLength = 64 * 1024;

    private InboundWebhookLog()
    {
        Source = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Source { get; private set; }
    public bool SignatureValid { get; private set; }
    public InboundWebhookStatus Status { get; private set; }
    public int PayloadSize { get; private set; }

    /// <summary>The body, kept only for an accepted call and cut at 64 KB; a refused call never stores what it carried.</summary>
    public string? Payload { get; private set; }

    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public static InboundWebhookLog Accepted(string source, string payload, int payloadSize, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Source = source,
        SignatureValid = true,
        Status = InboundWebhookStatus.Received,
        PayloadSize = payloadSize,
        Payload = payload.Length > MaxStoredPayloadLength ? payload[..MaxStoredPayloadLength] : payload,
        CreatedAt = now,
    };

    public static InboundWebhookLog Rejected(string source, string reason, int payloadSize, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Source = source,
        SignatureValid = false,
        Status = InboundWebhookStatus.Rejected,
        PayloadSize = payloadSize,
        Error = reason.Length > 200 ? reason[..200] : reason,
        CreatedAt = now,
    };

    public void MarkProcessed(DateTimeOffset now)
    {
        Status = InboundWebhookStatus.Processed;
        ProcessedAt = now;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        Status = InboundWebhookStatus.Failed;
        Error = error.Length > 200 ? error[..200] : error;
        ProcessedAt = now;
    }
}

/// <summary>One unit of background work, kept in the database so it survives a restart and can be counted and retried.</summary>
public sealed class BackgroundJobRecord
{
    public const int MaxErrorLength = 500;

    private BackgroundJobRecord()
    {
        Queue = string.Empty;
        Type = string.Empty;
        PayloadJson = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Queue { get; private set; }

    /// <summary>What the handler of the queue should do, for example <c>send</c> or <c>deliver</c>.</summary>
    public string Type { get; private set; }

    public string PayloadJson { get; private set; }
    public JobStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }

    /// <summary>When a waiting job may run (later than now while it waits for a retry).</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static BackgroundJobRecord Create(string queue, string type, string payloadJson, int maxAttempts, DateTimeOffset now, TimeSpan? delay = null)
    {
        if (!JobQueues.IsKnown(queue)) throw new DomainException($"Unknown queue '{queue}'.");
        if (string.IsNullOrWhiteSpace(type)) throw new DomainException("A job needs a type.");
        if (maxAttempts < 1) throw new DomainException("A job runs at least once.");

        return new BackgroundJobRecord
        {
            Id = Guid.NewGuid(),
            Queue = queue,
            Type = type,
            PayloadJson = payloadJson,
            Status = JobStatus.Pending,
            MaxAttempts = maxAttempts,
            NextAttemptAt = delay is { } wait && wait > TimeSpan.Zero ? now + wait : now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void MarkCompleted(DateTimeOffset now)
    {
        Status = JobStatus.Completed;
        LastError = null;
        CompletedAt = now;
        UpdatedAt = now;
    }

    /// <summary>The attempt failed. Returns true when another attempt is left (the job waits until <paramref name="retryAt"/>), false when it has failed for good.</summary>
    public bool MarkAttemptFailed(string error, DateTimeOffset now, DateTimeOffset retryAt, bool permanent = false)
    {
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        UpdatedAt = now;
        if (permanent || Attempts >= MaxAttempts)
        {
            Status = JobStatus.Failed;
            return false;
        }

        Status = JobStatus.Pending;
        NextAttemptAt = retryAt;
        return true;
    }

    /// <summary>Puts a failed job back in the queue with a fresh set of attempts.</summary>
    public void Requeue(DateTimeOffset now)
    {
        if (Status != JobStatus.Failed) throw new DomainException("Only a failed job can be retried.");
        Status = JobStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
        UpdatedAt = now;
    }

    /// <summary>A job found "processing" after a restart was cut off; it waits again (the attempt that was cut off stays counted).</summary>
    public void ResetInterrupted(DateTimeOffset now)
    {
        if (Status != JobStatus.Processing) return;
        Status = JobStatus.Pending;
        NextAttemptAt = now;
        UpdatedAt = now;
    }
}

/// <summary>The notification templates and projects every installation starts with.</summary>
public static class PlatformDefaults
{
    public const string ChatOfflineEmailTemplate = "ChatMessageOfflineEmail";
    public const string ChatOfflineChatTemplate = "ChatMessageOfflineChat";

    public static IReadOnlyList<(string Code, string Name, string Subject, string Body, NotificationChannel Channel, NotificationPriority Priority)> Templates { get; } =
    [
        ("UserRegistered", "Chào mừng người dùng mới", "Chào mừng {{UserName}} đến với {{AppName}}",
            "Xin chào {{UserName}},\n\nTài khoản của bạn đã được tạo. Hãy xác nhận địa chỉ email để bắt đầu: {{ActionUrl}}\n\nLiên kết có hiệu lực trong {{ExpiryMinutes}} phút.",
            NotificationChannel.Email, NotificationPriority.Normal),
        ("PasswordReset", "Khôi phục mật khẩu", "Đặt lại mật khẩu {{AppName}}",
            "Xin chào {{UserName}},\n\nĐể đặt lại mật khẩu, mở liên kết sau: {{ActionUrl}}\n\nLiên kết dùng một lần và hết hạn sau {{ExpiryMinutes}} phút. Nếu không phải bạn yêu cầu, hãy bỏ qua email này.",
            NotificationChannel.Email, NotificationPriority.Normal),
        ("SecurityAlert", "Cảnh báo bảo mật", "Cảnh báo bảo mật cho tài khoản {{UserName}}",
            "Phát hiện hoạt động bất thường trên tài khoản {{UserName}}: {{Details}}.\n\nNếu không phải bạn, hãy đổi mật khẩu ngay: {{ActionUrl}}",
            NotificationChannel.Both, NotificationPriority.Critical),
        ("SystemMaintenance", "Thông báo bảo trì hệ thống", "Bảo trì hệ thống {{AppName}} lúc {{StartsAt}}",
            "{{AppName}} sẽ được bảo trì từ {{StartsAt}}, dự kiến kéo dài {{DurationMinutes}} phút. Trong thời gian này dịch vụ có thể gián đoạn.",
            NotificationChannel.Both, NotificationPriority.High),
        ("PaymentSuccess", "Thanh toán thành công", "Đã nhận thanh toán {{Amount}} cho {{PlanName}}",
            "Xin chào {{UserName}},\n\nChúng tôi đã nhận thanh toán {{Amount}} cho gói {{PlanName}}. Mã giao dịch: {{TransactionId}}.",
            NotificationChannel.Email, NotificationPriority.Normal),
        // Sent by the chat service to a conversation member who is offline (the other stock templates are for the applications).
        (ChatOfflineEmailTemplate, "Tin nhắn mới (email)", "Tin nhắn mới từ {{SenderName}}",
            "🔔 Bạn có tin nhắn mới từ {{SenderName}}: '{{Preview}}' trong AppInHouse",
            NotificationChannel.Email, NotificationPriority.Normal),
        (ChatOfflineChatTemplate, "Tin nhắn mới (chat)", "Tin nhắn mới từ {{SenderName}}",
            "🔔 Bạn có tin nhắn mới từ {{SenderName}}: '{{Preview}}' trong AppInHouse",
            NotificationChannel.Chat, NotificationPriority.Normal),
    ];

    public static IReadOnlyList<(string Code, string Name, string Description, ProjectEnvironment Environment, int? Emails, int? Chats, long? StorageBytes)> Projects { get; } =
    [
        ("default", "Platform Core", "Không gian dùng chung của nền tảng; các khóa của dự án này có thể thao tác cho mọi dự án.", ProjectEnvironment.Production, null, null, null),
        ("identity", "Identity Platform", "Cổng xác thực tập trung OAuth2/OIDC và quản lý tài khoản.", ProjectEnvironment.Production, 5_000, 1_000, 5L * 1024 * 1024 * 1024),
        ("linguabridge", "LinguaBridge", "Nền tảng học ngoại ngữ đa ngôn ngữ.", ProjectEnvironment.Production, 10_000, 2_000, 50L * 1024 * 1024 * 1024),
        ("crm", "CRM Internal", "Quản lý khách hàng nội bộ.", ProjectEnvironment.Staging, 1_000, 500, 10L * 1024 * 1024 * 1024),
    ];
}
