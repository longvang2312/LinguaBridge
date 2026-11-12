using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Platform;

// What the platform services need from persistence and from the outside, and what they answer. Kept apart from the older
// repository interfaces so that adding a query here never breaks a test double of those.

public interface INotificationRepository
{
    Task<NotificationTemplate?> GetTemplateAsync(string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationTemplate>> ListTemplatesAsync(CancellationToken cancellationToken = default);
    void AddLog(NotificationLog log);
    Task<IReadOnlyList<NotificationLog>> ListLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default);
}

public interface IProjectRepository
{
    /// <summary>Tracked, so a change is saved by the unit of work.</summary>
    Task<ProjectTenant?> GetByCodeAsync(string projectCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectTenant>> ListAsync(CancellationToken cancellationToken = default);
    void Add(ProjectTenant tenant);
}

public interface IWebhookRepository
{
    void Add(WebhookSubscription subscription);
    void Remove(WebhookSubscription subscription);

    /// <summary>Tracked.</summary>
    Task<WebhookSubscription?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WebhookSubscription>> ListAsync(string? projectCode, CancellationToken cancellationToken = default);

    /// <summary>The active subscriptions that receive an event of <paramref name="projectCode"/>: its own and the platform's. Tracked.</summary>
    Task<IReadOnlyList<WebhookSubscription>> ListActiveForEventAsync(string eventName, string projectCode, CancellationToken cancellationToken = default);

    void AddInbound(InboundWebhookLog log);
    Task<InboundWebhookLog?> GetInboundAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InboundWebhookLog>> ListInboundAsync(string? source, int limit, CancellationToken cancellationToken = default);
}

/// <summary>The job (tracked) when it was taken; otherwise null and, for a job that is waiting for a retry, when to look again.</summary>
public sealed record JobClaim(BackgroundJobRecord? Job, DateTimeOffset? NotBefore);

public sealed record QueueCounts(string Queue, int Pending, int Processing, int CompletedSince, int Failed);

public interface IBackgroundJobRepository
{
    void Add(BackgroundJobRecord job);

    /// <summary>Tracked.</summary>
    Task<BackgroundJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a waiting job for this worker in one atomic step (pending and due becomes processing, with one more attempt counted), so two
    /// workers never run the same job. When it cannot be taken, <see cref="JobClaim.NotBefore"/> says when it will be due, if it is waiting.
    /// </summary>
    Task<JobClaim> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Pending and processing jobs, oldest first. Tracked.</summary>
    Task<IReadOnlyList<BackgroundJobRecord>> ListUnfinishedAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<BackgroundJobRecord>> ListFailedAsync(string queue, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueueCounts>> CountsAsync(DateTimeOffset completedSince, CancellationToken cancellationToken = default);
}

/// <summary>Counts for quotas, usage and metrics. Times are half-open: <c>from</c> included, <c>to</c> excluded.</summary>
public interface IUsageRepository
{
    Task<int> CountDeliveriesAsync(string? projectCode, IReadOnlyCollection<ChannelType> channels, DeliveryStatus? status, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<long> SumStorageBytesAsync(string? projectCode, CancellationToken cancellationToken = default);
    Task<int> CountFilesAsync(string? projectCode, CancellationToken cancellationToken = default);
    Task<int> CountMediaLogsAsync(string? status, DateTimeOffset from, CancellationToken cancellationToken = default);
    Task<int> CountInboundAsync(string? source, InboundWebhookStatus? status, DateTimeOffset from, CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> LastInboundAsync(string source, CancellationToken cancellationToken = default);
    Task<int> CountJobsCreatedAsync(DateTimeOffset from, CancellationToken cancellationToken = default);
    Task<int> CountActiveApiKeysAsync(string? projectCode, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<int> CountActiveConfigurationsAsync(IReadOnlyCollection<ChannelType> channels, CancellationToken cancellationToken = default);
    Task<int> CountNotificationsAsync(string? status, DateTimeOffset from, CancellationToken cancellationToken = default);
    Task<int> CountActiveTemplatesAsync(CancellationToken cancellationToken = default);
    Task<int> CountActiveProjectsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Deletes what is older than the retention period. Every method returns how many rows it removed.</summary>
public interface IRetentionRepository
{
    Task<int> DeleteDeliveryLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> DeleteMediaLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> DeleteSystemLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> DeleteNotificationLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> DeleteInboundLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> DeleteFinishedJobsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

public enum QuotaKind
{
    DailyEmail,
    DailyChat,
    Storage,
}

/// <summary>A project used up its quota. The API answers 429.</summary>
public sealed class QuotaExceededException(QuotaKind kind, long limit, long used, DateTimeOffset? resetsAt, string message) : Exception(message)
{
    public QuotaKind Kind { get; } = kind;
    public long Limit { get; } = limit;
    public long Used { get; } = used;

    /// <summary>When a daily quota starts again (the next UTC midnight); null for the storage quota, which only frees up when files are deleted.</summary>
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
}

/// <summary>The caller may not send through this channel (its key lacks the channel's scope). The API answers 403.</summary>
public sealed class ChannelNotPermittedException(string channel, string templateCode)
    : Exception($"This key may not send {channel}, which the template '{templateCode}' uses.")
{
    public string Channel { get; } = channel;
}

/// <summary>The project is deactivated. The API answers 403.</summary>
public sealed class ProjectInactiveException(string projectCode) : Exception($"Project '{projectCode}' is deactivated.")
{
    public string ProjectCode { get; } = projectCode;
}

/// <summary>A job that must not be tried again (a refusal, a missing configuration): it fails at once.</summary>
public sealed class JobPermanentFailureException(string message) : Exception(message);

// ------------------------------------------------------------------------------------------------ projects

public sealed record ProjectDto(
    string ProjectCode, string Name, string Description, string Environment, int? DailyEmailQuota, int? DailyChatQuota,
    long? StorageQuotaBytes, bool IsActive, DateTimeOffset CreatedAt);

public sealed record CreateProjectCommand(
    string ProjectCode, string Name, string? Description, ProjectEnvironment Environment,
    int? DailyEmailQuota, int? DailyChatQuota, long? StorageQuotaBytes);

public sealed record UpdateProjectCommand(
    string Name, string? Description, ProjectEnvironment Environment,
    int? DailyEmailQuota, int? DailyChatQuota, long? StorageQuotaBytes, bool IsActive);

/// <summary>One project's use of its limits today (UTC day) and in total.</summary>
public sealed record ProjectUsageDto(
    string ProjectCode, DateOnly Date, int EmailsSentToday, int? DailyEmailQuota, int ChatSentToday, int? DailyChatQuota,
    long StorageBytes, long? StorageQuotaBytes, int FileCount, int ActiveApiKeys);

public interface IProjectManager
{
    /// <summary>Every project, or only <paramref name="onlyProject"/> (what a key of one project may see).</summary>
    Task<IReadOnlyList<ProjectDto>> ListAsync(string? onlyProject, CancellationToken cancellationToken = default);

    Task<ProjectDto> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken = default);

    Task<ProjectDto> UpdateAsync(string projectCode, UpdateProjectCommand command, CancellationToken cancellationToken = default);

    Task<ProjectUsageDto> GetUsageAsync(string projectCode, CancellationToken cancellationToken = default);
}

/// <summary>Checked before an email, a chat message or an upload is accepted.</summary>
public interface IQuotaGuard
{
    /// <exception cref="QuotaExceededException">The project has sent as many as its daily quota allows.</exception>
    /// <exception cref="ProjectInactiveException">The project is deactivated.</exception>
    Task EnsureCanSendAsync(string projectCode, ChannelType channel, CancellationToken cancellationToken = default);

    /// <summary>Storing <paramref name="incomingBytes"/> more would go over the storage quota (0 asks only whether there is any room at all).</summary>
    Task EnsureCanStoreAsync(string projectCode, long incomingBytes, CancellationToken cancellationToken = default);
}

// ------------------------------------------------------------------------------------------------ events and jobs

/// <summary>Tells the subscribed applications that something happened. Never throws: an event must not fail the work that caused it.</summary>
public interface IEventPublisher
{
    Task PublishAsync(string eventName, string projectCode, object data, CancellationToken cancellationToken = default);
}

public sealed record BackgroundJobItem(Guid JobId, string Queue);

/// <summary>The in-memory side of the queues: one channel per queue that wakes the workers. The truth is the job table; a lost item is found again at the next start.</summary>
public interface IJobChannels
{
    void Write(BackgroundJobItem item);

    IAsyncEnumerable<BackgroundJobItem> ReadAllAsync(string queue, CancellationToken cancellationToken);
}

/// <summary>How the engine behaves: the waits between attempts (an attempt is made, then each wait is followed by another), the workers, the retention.</summary>
public sealed class JobSettings
{
    public const string SectionName = "Jobs";

    /// <summary>The waits before the second, third, ... attempt. Null means the default 1 s, 5 s, 15 s, which makes four attempts in all.</summary>
    public int[]? RetryDelaysSeconds { get; set; }

    private int[] Delays => RetryDelaysSeconds ?? [1, 5, 15];

    public int WorkersPerQueue { get; set; } = 2;
    public int LogRetentionDays { get; set; } = 90;
    public int FinishedJobRetentionDays { get; set; } = 7;

    /// <summary>How long a metrics snapshot may be reused (the scheduled aggregation refreshes it every five minutes).</summary>
    public int MetricsCacheSeconds { get; set; } = 60;

    public bool SchedulesEnabled { get; set; } = true;

    public int MaxAttempts => Math.Max(1, Delays.Length + 1);

    /// <summary>The wait before attempt number <paramref name="attempt"/> + 1, after <paramref name="attempt"/> attempts have failed.</summary>
    public TimeSpan DelayAfter(int attempt) =>
        TimeSpan.FromSeconds(Delays.Length == 0 ? 0 : Delays[Math.Clamp(attempt - 1, 0, Delays.Length - 1)]);
}

public sealed record JobQueueStatusDto(string Name, string Purpose, int Pending, int Processing, int CompletedLast24h, int Failed, string Status);

public sealed record RetryFailedResult(string Queue, int Requeued);

public interface IJobQueue
{
    /// <summary>Saves the job and wakes the worker of the queue. <paramref name="payload"/> is stored as JSON. With <paramref name="delay"/> the job is not run before that time has passed.</summary>
    Task<Guid> EnqueueAsync(string queue, string type, object payload, CancellationToken cancellationToken = default, TimeSpan? delay = null);

    Task<IReadOnlyList<JobQueueStatusDto>> GetQueuesAsync(CancellationToken cancellationToken = default);

    /// <exception cref="Common.NotFoundException">There is no such queue.</exception>
    Task<RetryFailedResult> RetryFailedAsync(string queue, CancellationToken cancellationToken = default);
}

/// <summary>The work of one queue. A handler that throws makes the job wait and try again; <see cref="JobPermanentFailureException"/> makes it fail at once.</summary>
public interface IJobHandler
{
    string Queue { get; }

    Task HandleAsync(BackgroundJobRecord job, CancellationToken cancellationToken);
}

// ------------------------------------------------------------------------------------------------ scheduled tasks

public sealed record ScheduledTaskDto(
    string Id, string Name, string Description, string Cron, DateTimeOffset? LastRunAt, DateTimeOffset NextRunAt,
    string? LastStatus, string? LastError, int Runs, bool IsRunning);

/// <summary>A task that runs at times given by a cron expression (UTC). Every run gets its own scope.</summary>
public interface IScheduledTask
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Cron { get; }

    /// <summary>What the run did, in a few words (shown as its last status).</summary>
    Task<string> RunAsync(CancellationToken cancellationToken);
}

public interface IScheduleService
{
    Task<IReadOnlyList<ScheduledTaskDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <exception cref="Common.NotFoundException">There is no such task.</exception>
    Task<ScheduledTaskDto> RunNowAsync(string id, CancellationToken cancellationToken = default);
}

// ------------------------------------------------------------------------------------------------ monitoring

public sealed record ServiceHealthDto(string Id, string Name, string Status, string Detail, IReadOnlyDictionary<string, string> Metrics);

public sealed record HealthStatusDto(string Overall, DateTimeOffset GeneratedAt, IReadOnlyList<ServiceHealthDto> Services);

/// <summary>
/// The figures of the last 24 hours across the eight services. <see cref="TotalRequests24h"/> counts what the service did for
/// its callers: deliveries, file operations, inbound webhooks and queued jobs (it does not count reads of lists and logs).
/// </summary>
public sealed record SystemMetricsOverviewDto(
    DateTimeOffset GeneratedAt, DateTimeOffset StartedAt, long UptimeSeconds,
    int ServicesTotal, int ServicesHealthy, int ServicesDegraded, int ServicesIdle,
    long TotalRequests24h, int DeliveriesSent24h, int DeliveriesFailed24h, double? SuccessRatePercent,
    long StorageBytes, int FileCount, int ActiveApiKeys, int ActiveProjects, int PendingJobs, int FailedJobs,
    IReadOnlyList<ServiceHealthDto> Services);

public interface IMetricsCollector
{
    Task<SystemMetricsOverviewDto> GetOverviewAsync(bool refresh = false, CancellationToken cancellationToken = default);

    Task<HealthStatusDto> GetHealthAsync(CancellationToken cancellationToken = default);
}
