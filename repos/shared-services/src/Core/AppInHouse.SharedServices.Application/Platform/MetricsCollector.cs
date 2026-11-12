using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Platform;

/// <summary>When the service started and the last metrics it computed (reused for <see cref="JobSettings.MetricsCacheSeconds"/>).</summary>
public sealed class MetricsSnapshotStore(TimeProvider clock)
{
    private readonly Lock _lock = new();
    private SystemMetricsOverviewDto? _snapshot;

    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();

    public SystemMetricsOverviewDto? GetFresh(TimeSpan maxAge)
    {
        lock (_lock) return _snapshot is { } s && clock.GetUtcNow() - s.GeneratedAt <= maxAge ? s : null;
    }

    public void Set(SystemMetricsOverviewDto snapshot)
    {
        lock (_lock) _snapshot = snapshot;
    }
}

/// <summary>
/// Collects the figures of the last 24 hours and judges each of the eight services:
/// <b>Idle</b> when it has nothing to work with (no active configuration, no template, no subscription),
/// <b>Degraded</b> when it has done at least five things in 24 hours and more than a fifth of them failed (for jobs: any failed job is waiting),
/// <b>Healthy</b> otherwise.
/// </summary>
public sealed class MetricsCollector(
    IUsageRepository usage,
    IBackgroundJobRepository jobs,
    IWebhookRepository webhooks,
    MetricsSnapshotStore store,
    JobSettings settings,
    TimeProvider clock) : IMetricsCollector
{
    private const int MinimumSample = 5;

    public async Task<SystemMetricsOverviewDto> GetOverviewAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && store.GetFresh(TimeSpan.FromSeconds(Math.Max(settings.MetricsCacheSeconds, 0))) is { } cached) return cached;

        var overview = await ComputeAsync(cancellationToken);
        store.Set(overview);
        return overview;
    }

    public async Task<HealthStatusDto> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var overview = await GetOverviewAsync(cancellationToken: cancellationToken);
        var overall = overview.ServicesDegraded > 0 ? "Degraded" : overview.ServicesHealthy == 0 ? "Idle" : "Healthy";
        return new HealthStatusDto(overall, overview.GeneratedAt, overview.Services);
    }

    private async Task<SystemMetricsOverviewDto> ComputeAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since = now.AddHours(-24);
        var until = now.AddMinutes(5);
        ChannelType[] email = [ChannelType.Email];
        var chat = ProviderCatalog.ChatChannels;

        var emailSent = await usage.CountDeliveriesAsync(null, email, DeliveryStatus.Sent, since, until, ct);
        var emailFailed = await usage.CountDeliveriesAsync(null, email, DeliveryStatus.Failed, since, until, ct);
        var chatSent = await usage.CountDeliveriesAsync(null, chat, DeliveryStatus.Sent, since, until, ct);
        var chatFailed = await usage.CountDeliveriesAsync(null, chat, DeliveryStatus.Failed, since, until, ct);

        var mediaFailed = await usage.CountMediaLogsAsync("Failed", since, ct);
        var mediaAll = await usage.CountMediaLogsAsync(null, since, ct);
        var storageBytes = await usage.SumStorageBytesAsync(null, ct);
        var files = await usage.CountFilesAsync(null, ct);

        var notificationsAll = await usage.CountNotificationsAsync(null, since, ct);
        var notificationsFailed = await usage.CountNotificationsAsync("Failed", since, ct);
        var templates = await usage.CountActiveTemplatesAsync(ct);

        var counts = await jobs.CountsAsync(since, ct);
        var pendingJobs = counts.Sum(c => c.Pending);
        var failedJobs = counts.Sum(c => c.Failed);
        var jobsCreated = await usage.CountJobsCreatedAsync(since, ct);

        var subscriptions = await webhooks.ListAsync(null, ct);
        var activeSubscriptions = subscriptions.Count(s => s.IsActive);
        var outboundOk = subscriptions.Sum(s => s.SuccessCount);
        var outboundFailed = subscriptions.Sum(s => s.FailureCount);
        var inboundAccepted = 0;
        var inboundRejected = 0;
        foreach (var (source, _, _) in InboundSources.All)
        {
            inboundRejected += await usage.CountInboundAsync(source, InboundWebhookStatus.Rejected, since, ct);
            inboundAccepted += await usage.CountInboundAsync(source, null, since, ct) - await usage.CountInboundAsync(source, InboundWebhookStatus.Rejected, since, ct);
        }

        var activeProjects = await usage.CountActiveProjectsAsync(ct);
        var activeKeys = await usage.CountActiveApiKeysAsync(null, now, ct);
        var emailConfigs = await usage.CountActiveConfigurationsAsync(email, ct);
        var chatConfigs = await usage.CountActiveConfigurationsAsync(chat, ct);
        var storageConfigs = await usage.CountActiveConfigurationsAsync([ChannelType.Storage], ct);

        var services = new List<ServiceHealthDto>
        {
            Service("email", "Email Service", emailConfigs == 0, emailSent, emailFailed,
                emailConfigs == 0 ? "No active email configuration." : $"{emailSent} sent, {emailFailed} failed in 24 hours.",
                ("activeConfigurations", emailConfigs), ("sent24h", emailSent), ("failed24h", emailFailed)),
            Service("chat", "Chat Service", chatConfigs == 0, chatSent, chatFailed,
                chatConfigs == 0 ? "No active chat configuration." : $"{chatSent} sent, {chatFailed} failed in 24 hours.",
                ("activeConfigurations", chatConfigs), ("sent24h", chatSent), ("failed24h", chatFailed)),
            Service("storage", "Media & Storage Service", storageConfigs == 0, mediaAll - mediaFailed, mediaFailed,
                storageConfigs == 0 ? "No active storage configuration." : $"{files} files, {mediaFailed} failed operations in 24 hours.",
                ("activeConfigurations", storageConfigs), ("files", files), ("failedOperations24h", mediaFailed)),
            Service("projects", "Project & Access Service", activeProjects == 0, 0, 0,
                activeProjects == 0 ? "No active project." : $"{activeProjects} active projects, {activeKeys} active API keys.",
                ("activeProjects", activeProjects), ("activeApiKeys", activeKeys)),
            Service("notifications", "Notification Service", templates == 0, notificationsAll - notificationsFailed, notificationsFailed,
                templates == 0 ? "No active template." : $"{templates} templates, {notificationsAll} dispatched in 24 hours ({notificationsFailed} failed).",
                ("activeTemplates", templates), ("dispatched24h", notificationsAll), ("failed24h", notificationsFailed)),
            new ServiceHealthDto("jobs", "Background Job Service", failedJobs > 0 ? "Degraded" : "Healthy",
                failedJobs > 0 ? $"{failedJobs} failed jobs are waiting for a retry." : $"{pendingJobs} jobs waiting, none failed.",
                Metrics(("pending", pendingJobs), ("failed", failedJobs), ("created24h", jobsCreated))),
            Service("webhooks", "Webhook Service", activeSubscriptions == 0 && inboundAccepted + inboundRejected == 0, outboundOk, outboundFailed,
                $"{activeSubscriptions} subscriptions, {inboundAccepted} inbound calls accepted and {inboundRejected} refused in 24 hours.",
                ("activeSubscriptions", activeSubscriptions), ("inboundAccepted24h", inboundAccepted), ("inboundRejected24h", inboundRejected)),
            new ServiceHealthDto("monitoring", "Monitoring & Audit Service", "Healthy", "The database answers.", new Dictionary<string, string>()),
        };

        var sent = emailSent + chatSent;
        var failed = emailFailed + chatFailed;
        var total = sent + failed;
        var requests = total + mediaAll + inboundAccepted + inboundRejected + jobsCreated;

        return new SystemMetricsOverviewDto(
            now, store.StartedAt, (long)Math.Max((now - store.StartedAt).TotalSeconds, 0),
            services.Count, services.Count(s => s.Status == "Healthy"), services.Count(s => s.Status == "Degraded"), services.Count(s => s.Status == "Idle"),
            requests, sent, failed, total == 0 ? null : Math.Round(100.0 * sent / total, 1),
            storageBytes, files, activeKeys, activeProjects, pendingJobs, failedJobs, services);
    }

    private static ServiceHealthDto Service(string id, string name, bool idle, int ok, int failed, string detail, params (string Key, int Value)[] metrics)
    {
        var total = ok + failed;
        var status = idle ? "Idle" : total >= MinimumSample && failed * 5 > total ? "Degraded" : "Healthy";
        return new ServiceHealthDto(id, name, status, detail, Metrics(metrics));
    }

    private static Dictionary<string, string> Metrics(params (string Key, int Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value.ToString());
}
