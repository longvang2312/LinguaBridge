using AppInHouse.SharedServices.Application.Notifications;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests.Support;

internal sealed class FakeNotificationRepository : INotificationRepository
{
    public List<NotificationTemplate> Templates { get; } = [];
    public List<NotificationLog> Logs { get; } = [];

    public Task<NotificationTemplate?> GetTemplateAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(Templates.FirstOrDefault(t => t.Code == code));

    public Task<IReadOnlyList<NotificationTemplate>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationTemplate>>([.. Templates]);

    public void AddLog(NotificationLog log) => Logs.Add(log);

    public Task<IReadOnlyList<NotificationLog>> ListLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationLog>>([.. Logs.Where(l => projectCode is null || l.ProjectCode == projectCode).OrderByDescending(l => l.CreatedAt).Take(limit)]);
}

/// <summary>Records what each channel was asked to send and answers as told; a gate can hold an answer back to prove two channels run side by side.</summary>
internal sealed class FakeChannelSender : INotificationChannelSender
{
    public sealed record EmailCall(string Project, string To, string Subject, string Html, string Text);

    public sealed record ChatCall(string Project, string? Recipient, string Text);

    public List<EmailCall> Emails { get; } = [];
    public List<ChatCall> Chats { get; } = [];
    public Func<ChannelSendOutcome> EmailResult { get; set; } = () => new ChannelSendOutcome(true, null, Guid.NewGuid());
    public Func<ChannelSendOutcome> ChatResult { get; set; } = () => new ChannelSendOutcome(true, null, Guid.NewGuid());

    /// <summary>When set, each channel waits here after it has been asked, so the test can see whether the other one was asked meanwhile.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<ChannelSendOutcome> SendEmailAsync(string projectCode, string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken)
    {
        Emails.Add(new EmailCall(projectCode, to, subject, htmlBody, textBody));
        if (Gate is not null) await Gate.Task;
        return EmailResult();
    }

    public async Task<ChannelSendOutcome> SendChatAsync(string projectCode, string? recipient, string text, CancellationToken cancellationToken)
    {
        Chats.Add(new ChatCall(projectCode, recipient, text));
        if (Gate is not null) await Gate.Task;
        return ChatResult();
    }

    public static ChannelSendOutcome Failure(string error) => new(false, error, Guid.NewGuid());
}

internal sealed class FakeJobQueue : IJobQueue
{
    public sealed record Enqueued(Guid Id, string Queue, string Type, object Payload, TimeSpan? Delay = null);

    public List<Enqueued> Jobs { get; } = [];
    public List<string> Retried { get; } = [];

    public Task<Guid> EnqueueAsync(string queue, string type, object payload, CancellationToken cancellationToken = default, TimeSpan? delay = null)
    {
        var job = new Enqueued(Guid.NewGuid(), queue, type, payload, delay);
        Jobs.Add(job);
        return Task.FromResult(job.Id);
    }

    public Task<IReadOnlyList<JobQueueStatusDto>> GetQueuesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JobQueueStatusDto>>([]);

    public Task<RetryFailedResult> RetryFailedAsync(string queue, CancellationToken cancellationToken = default)
    {
        Retried.Add(queue);
        return Task.FromResult(new RetryFailedResult(queue, 0));
    }
}

internal sealed class FakeProjectRepository : IProjectRepository
{
    public List<ProjectTenant> Rows { get; } = [];

    public Task<ProjectTenant?> GetByCodeAsync(string projectCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.FirstOrDefault(p => p.ProjectCode == projectCode));

    public Task<IReadOnlyList<ProjectTenant>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectTenant>>([.. Rows]);

    public void Add(ProjectTenant tenant) => Rows.Add(tenant);
}

/// <summary>Every count is whatever the test sets; it also records how it was asked.</summary>
internal sealed class FakeUsageRepository : IUsageRepository
{
    public int Delivered { get; set; }
    public long StorageBytes { get; set; }
    public int Files { get; set; }
    public int MediaAll { get; set; }
    public int MediaFailed { get; set; }
    public int ActiveKeys { get; set; }
    public int ActiveConfigurations { get; set; } = 1;
    public int NotificationsAll { get; set; }
    public int NotificationsFailed { get; set; }
    public int ActiveTemplates { get; set; } = 1;
    public int ActiveProjects { get; set; } = 1;
    public int JobsCreated { get; set; }
    public Func<IReadOnlyCollection<ChannelType>, DeliveryStatus?, int>? Deliveries { get; set; }
    public Func<string?, InboundWebhookStatus?, int>? Inbound { get; set; }

    public List<(string? Project, IReadOnlyCollection<ChannelType> Channels, DeliveryStatus? Status, DateTimeOffset From, DateTimeOffset To)> DeliveryQueries { get; } = [];

    public Task<int> CountDeliveriesAsync(string? projectCode, IReadOnlyCollection<ChannelType> channels, DeliveryStatus? status, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        DeliveryQueries.Add((projectCode, channels, status, from, to));
        return Task.FromResult(Deliveries?.Invoke(channels, status) ?? Delivered);
    }

    public Task<long> SumStorageBytesAsync(string? projectCode, CancellationToken cancellationToken = default) => Task.FromResult(StorageBytes);
    public Task<int> CountFilesAsync(string? projectCode, CancellationToken cancellationToken = default) => Task.FromResult(Files);
    public Task<int> CountMediaLogsAsync(string? status, DateTimeOffset from, CancellationToken cancellationToken = default) => Task.FromResult(status == "Failed" ? MediaFailed : MediaAll);
    public Task<int> CountInboundAsync(string? source, InboundWebhookStatus? status, DateTimeOffset from, CancellationToken cancellationToken = default) => Task.FromResult(Inbound?.Invoke(source, status) ?? 0);
    public Task<DateTimeOffset?> LastInboundAsync(string source, CancellationToken cancellationToken = default) => Task.FromResult<DateTimeOffset?>(null);
    public Task<int> CountJobsCreatedAsync(DateTimeOffset from, CancellationToken cancellationToken = default) => Task.FromResult(JobsCreated);
    public Task<int> CountActiveApiKeysAsync(string? projectCode, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult(ActiveKeys);
    public Task<int> CountActiveConfigurationsAsync(IReadOnlyCollection<ChannelType> channels, CancellationToken cancellationToken = default) => Task.FromResult(ActiveConfigurations);
    public Task<int> CountNotificationsAsync(string? status, DateTimeOffset from, CancellationToken cancellationToken = default) => Task.FromResult(status == "Failed" ? NotificationsFailed : NotificationsAll);
    public Task<int> CountActiveTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(ActiveTemplates);
    public Task<int> CountActiveProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult(ActiveProjects);
}

internal sealed class FakeBackgroundJobRepository : IBackgroundJobRepository
{
    public List<BackgroundJobRecord> Rows { get; } = [];
    public List<QueueCounts> Counts { get; } = [];

    public void Add(BackgroundJobRecord job) => Rows.Add(job);

    public Task<BackgroundJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Rows.FirstOrDefault(j => j.Id == id));

    public Task<JobClaim> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException("The claim is tested against the real database.");

    public Task<IReadOnlyList<BackgroundJobRecord>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BackgroundJobRecord>>([.. Rows.Where(j => j.Status is JobStatus.Pending or JobStatus.Processing)]);

    public Task<IReadOnlyList<BackgroundJobRecord>> ListFailedAsync(string queue, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BackgroundJobRecord>>([.. Rows.Where(j => j.Queue == queue && j.Status == JobStatus.Failed)]);

    public Task<IReadOnlyList<QueueCounts>> CountsAsync(DateTimeOffset completedSince, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<QueueCounts>>([.. Counts]);
}

internal sealed class FakeJobChannels : IJobChannels
{
    public List<BackgroundJobItem> Written { get; } = [];

    /// <summary>Called on every write, so a test can see what the repository held at that moment (saving must come first).</summary>
    public Action<BackgroundJobItem>? OnWrite { get; set; }

    public void Write(BackgroundJobItem item)
    {
        OnWrite?.Invoke(item);
        Written.Add(item);
    }

    public IAsyncEnumerable<BackgroundJobItem> ReadAllAsync(string queue, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeWebhookRepository : IWebhookRepository
{
    public List<WebhookSubscription> Rows { get; } = [];
    public List<InboundWebhookLog> Inbound { get; } = [];

    public void Add(WebhookSubscription subscription) => Rows.Add(subscription);
    public void Remove(WebhookSubscription subscription) => Rows.Remove(subscription);
    public Task<WebhookSubscription?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Rows.FirstOrDefault(s => s.Id == id));

    public Task<IReadOnlyList<WebhookSubscription>> ListAsync(string? projectCode, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WebhookSubscription>>([.. Rows.Where(s => projectCode is null || s.ProjectCode == projectCode)]);

    public Task<IReadOnlyList<WebhookSubscription>> ListActiveForEventAsync(string eventName, string projectCode, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WebhookSubscription>>([.. Rows.Where(s => s.IsActive && (s.ProjectCode == projectCode || s.ProjectCode == ProjectTenant.PlatformCode) && s.Wants(eventName))]);

    public void AddInbound(InboundWebhookLog log) => Inbound.Add(log);
    public Task<InboundWebhookLog?> GetInboundAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Inbound.FirstOrDefault(l => l.Id == id));

    public Task<IReadOnlyList<InboundWebhookLog>> ListInboundAsync(string? source, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InboundWebhookLog>>([.. Inbound.Where(l => source is null || l.Source == source).Take(limit)]);
}

internal sealed class FakeWebhookSender : IWebhookSender
{
    public sealed record Post(string Url, string Body, IReadOnlyDictionary<string, string> Headers);

    public List<Post> Posts { get; } = [];
    public string? Blocked { get; set; }
    public Func<WebhookHttpResult> Result { get; set; } = () => new WebhookHttpResult(200, null);

    public Task<string?> CheckTargetAsync(string url, CancellationToken cancellationToken) => Task.FromResult(Blocked);

    public Task<WebhookHttpResult> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        Posts.Add(new Post(url, body, headers));
        return Task.FromResult(Result());
    }
}

internal sealed class FakeInboundSecrets : IInboundSecretProvider
{
    public Dictionary<string, string> Secrets { get; } = [];

    public string? GetSecret(string source) => Secrets.GetValueOrDefault(source);
}

internal sealed class FakeRetentionRepository : IRetentionRepository
{
    public Dictionary<string, DateTimeOffset> Cutoffs { get; } = [];

    private Task<int> Record(string table, DateTimeOffset cutoff)
    {
        Cutoffs[table] = cutoff;
        return Task.FromResult(2);
    }

    public Task<int> DeleteDeliveryLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Record("delivery", cutoff);
    public Task<int> DeleteMediaLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Record("media", cutoff);
    public Task<int> DeleteSystemLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Record("system", cutoff);
    public Task<int> DeleteNotificationLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Record("notification", cutoff);
    public Task<int> DeleteInboundLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Record("inbound", cutoff);
    public Task<int> DeleteFinishedJobsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Record("jobs", cutoff);
}
