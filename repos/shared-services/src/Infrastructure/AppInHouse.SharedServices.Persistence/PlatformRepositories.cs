using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.Persistence;

internal sealed class NotificationRepository(SharedServicesDbContext db) : INotificationRepository
{
    public Task<NotificationTemplate?> GetTemplateAsync(string code, CancellationToken cancellationToken = default) =>
        db.NotificationTemplates.FirstOrDefaultAsync(t => t.Code == code, cancellationToken);

    public async Task<IReadOnlyList<NotificationTemplate>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        await db.NotificationTemplates.AsNoTracking().OrderBy(t => t.Code).ToListAsync(cancellationToken);

    public void AddLog(NotificationLog log) => db.NotificationLogs.Add(log);

    public async Task<IReadOnlyList<NotificationLog>> ListLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default)
    {
        var query = db.NotificationLogs.AsNoTracking();
        if (projectCode is not null) query = query.Where(l => l.ProjectCode == projectCode);
        return await query.OrderByDescending(l => l.CreatedAt).Take(limit).ToListAsync(cancellationToken);
    }
}

internal sealed class ProjectRepository(SharedServicesDbContext db) : IProjectRepository
{
    public Task<ProjectTenant?> GetByCodeAsync(string projectCode, CancellationToken cancellationToken = default) =>
        db.ProjectTenants.FirstOrDefaultAsync(p => p.ProjectCode == projectCode, cancellationToken);

    public async Task<IReadOnlyList<ProjectTenant>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.ProjectTenants.AsNoTracking().ToListAsync(cancellationToken);

    public void Add(ProjectTenant tenant) => db.ProjectTenants.Add(tenant);
}

internal sealed class WebhookRepository(SharedServicesDbContext db) : IWebhookRepository
{
    public void Add(WebhookSubscription subscription) => db.WebhookSubscriptions.Add(subscription);

    public void Remove(WebhookSubscription subscription) => db.WebhookSubscriptions.Remove(subscription);

    public Task<WebhookSubscription?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WebhookSubscription>> ListAsync(string? projectCode, CancellationToken cancellationToken = default)
    {
        var query = db.WebhookSubscriptions.AsNoTracking();
        if (projectCode is not null) query = query.Where(s => s.ProjectCode == projectCode);
        return await query.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListActiveForEventAsync(string eventName, string projectCode, CancellationToken cancellationToken = default)
    {
        var candidates = await db.WebhookSubscriptions
            .Where(s => s.IsActive && (s.ProjectCode == projectCode || s.ProjectCode == ProjectTenant.PlatformCode))
            .ToListAsync(cancellationToken);
        return [.. candidates.Where(s => s.Wants(eventName))];
    }

    public void AddInbound(InboundWebhookLog log) => db.InboundWebhookLogs.Add(log);

    public Task<InboundWebhookLog?> GetInboundAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.InboundWebhookLogs.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

    public async Task<IReadOnlyList<InboundWebhookLog>> ListInboundAsync(string? source, int limit, CancellationToken cancellationToken = default)
    {
        var query = db.InboundWebhookLogs.AsNoTracking();
        if (source is not null) query = query.Where(l => l.Source == source);
        return await query.OrderByDescending(l => l.CreatedAt).Take(limit).ToListAsync(cancellationToken);
    }
}

internal sealed class BackgroundJobRepository(SharedServicesDbContext db) : IBackgroundJobRepository
{
    public void Add(BackgroundJobRecord job) => db.BackgroundJobs.Add(job);

    public Task<BackgroundJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.BackgroundJobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

    public async Task<JobClaim> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // One UPDATE decides who gets the job: only one of two workers that race for it sees a row change.
        var taken = await db.BackgroundJobs
            .Where(j => j.Id == id && j.Status == JobStatus.Pending && j.NextAttemptAt <= now)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Processing)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.UpdatedAt, now), cancellationToken);

        if (taken == 1) return new JobClaim(await db.BackgroundJobs.FirstAsync(j => j.Id == id, cancellationToken), null);

        var waiting = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == id && j.Status == JobStatus.Pending)
            .Select(j => new { j.NextAttemptAt }).FirstOrDefaultAsync(cancellationToken);
        return new JobClaim(null, waiting?.NextAttemptAt);
    }

    public async Task<IReadOnlyList<BackgroundJobRecord>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        await db.BackgroundJobs.Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Processing).OrderBy(j => j.CreatedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BackgroundJobRecord>> ListFailedAsync(string queue, CancellationToken cancellationToken = default) =>
        await db.BackgroundJobs.Where(j => j.Queue == queue && j.Status == JobStatus.Failed).OrderBy(j => j.CreatedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<QueueCounts>> CountsAsync(DateTimeOffset completedSince, CancellationToken cancellationToken = default)
    {
        var rows = await db.BackgroundJobs.AsNoTracking()
            .GroupBy(j => j.Queue)
            .Select(g => new
            {
                Queue = g.Key,
                Pending = g.Count(j => j.Status == JobStatus.Pending),
                Processing = g.Count(j => j.Status == JobStatus.Processing),
                Completed = g.Count(j => j.Status == JobStatus.Completed && j.CompletedAt >= completedSince),
                Failed = g.Count(j => j.Status == JobStatus.Failed),
            })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new QueueCounts(r.Queue, r.Pending, r.Processing, r.Completed, r.Failed))];
    }
}

internal sealed class UsageRepository(SharedServicesDbContext db) : IUsageRepository
{
    public Task<int> CountDeliveriesAsync(
        string? projectCode, IReadOnlyCollection<ChannelType> channels, DeliveryStatus? status, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var wanted = channels.ToList();   // a plain list, which EF turns into IN (...)
        var query = db.DeliveryLogs.AsNoTracking().Where(l => wanted.Contains(l.ChannelType) && l.CreatedAt >= from && l.CreatedAt < to);
        if (projectCode is not null) query = query.Where(l => l.ProjectCode == projectCode);
        if (status is { } s) query = query.Where(l => l.Status == s);
        return query.CountAsync(cancellationToken);
    }

    public async Task<long> SumStorageBytesAsync(string? projectCode, CancellationToken cancellationToken = default)
    {
        var query = db.StoredFiles.AsNoTracking();
        if (projectCode is not null) query = query.Where(f => f.ProjectCode == projectCode);
        return await query.SumAsync(f => (long?)f.FileSizeBytes, cancellationToken) ?? 0;
    }

    public Task<int> CountFilesAsync(string? projectCode, CancellationToken cancellationToken = default)
    {
        var query = db.StoredFiles.AsNoTracking();
        if (projectCode is not null) query = query.Where(f => f.ProjectCode == projectCode);
        return query.CountAsync(cancellationToken);
    }

    public Task<int> CountMediaLogsAsync(string? status, DateTimeOffset from, CancellationToken cancellationToken = default)
    {
        var query = db.MediaLogs.AsNoTracking().Where(l => l.CreatedAt >= from);
        if (status is not null) query = query.Where(l => l.Status == status);
        return query.CountAsync(cancellationToken);
    }

    public Task<int> CountInboundAsync(string? source, InboundWebhookStatus? status, DateTimeOffset from, CancellationToken cancellationToken = default)
    {
        var query = db.InboundWebhookLogs.AsNoTracking().Where(l => l.CreatedAt >= from);
        if (source is not null) query = query.Where(l => l.Source == source);
        if (status is { } s) query = query.Where(l => l.Status == s);
        return query.CountAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> LastInboundAsync(string source, CancellationToken cancellationToken = default)
    {
        var latest = await db.InboundWebhookLogs.AsNoTracking()
            .Where(l => l.Source == source && l.Status != InboundWebhookStatus.Rejected)
            .OrderByDescending(l => l.CreatedAt).Select(l => new { l.CreatedAt }).FirstOrDefaultAsync(cancellationToken);
        return latest?.CreatedAt;
    }

    public Task<int> CountJobsCreatedAsync(DateTimeOffset from, CancellationToken cancellationToken = default) =>
        db.BackgroundJobs.AsNoTracking().CountAsync(j => j.CreatedAt >= from, cancellationToken);

    public Task<int> CountActiveApiKeysAsync(string? projectCode, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var query = db.ApiKeys.AsNoTracking().Where(k => !k.IsRevoked && (k.ExpiresAt == null || k.ExpiresAt > now));
        if (projectCode is not null) query = query.Where(k => k.ProjectCode == projectCode);
        return query.CountAsync(cancellationToken);
    }

    public Task<int> CountActiveConfigurationsAsync(IReadOnlyCollection<ChannelType> channels, CancellationToken cancellationToken = default)
    {
        var wanted = channels.ToList();
        return db.ChannelConfigurations.AsNoTracking().CountAsync(c => c.Status == ConfigurationStatus.Active && wanted.Contains(c.ChannelType), cancellationToken);
    }

    public Task<int> CountNotificationsAsync(string? status, DateTimeOffset from, CancellationToken cancellationToken = default)
    {
        var query = db.NotificationLogs.AsNoTracking().Where(l => l.CreatedAt >= from);
        if (status is not null) query = query.Where(l => l.Status == status);
        return query.CountAsync(cancellationToken);
    }

    public Task<int> CountActiveTemplatesAsync(CancellationToken cancellationToken = default) =>
        db.NotificationTemplates.AsNoTracking().CountAsync(t => t.IsActive, cancellationToken);

    public Task<int> CountActiveProjectsAsync(CancellationToken cancellationToken = default) =>
        db.ProjectTenants.AsNoTracking().CountAsync(p => p.IsActive, cancellationToken);
}

internal sealed class RetentionRepository(SharedServicesDbContext db) : IRetentionRepository
{
    public Task<int> DeleteDeliveryLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        db.DeliveryLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteMediaLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        db.MediaLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteSystemLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        db.SystemLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteNotificationLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        db.NotificationLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteInboundLogsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        db.InboundWebhookLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteFinishedJobsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        db.BackgroundJobs.Where(j => j.Status == JobStatus.Completed && j.CompletedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
}

/// <summary>Fills in what an installation starts with (the notification templates and the core projects). Only what is missing is added: a template or a project somebody changed is never overwritten.</summary>
internal static class PlatformSeeder
{
    public static async Task SeedAsync(SharedServicesDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var templates = (await db.NotificationTemplates.Select(t => t.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var (code, name, subject, body, channel, priority) in PlatformDefaults.Templates.Where(t => !templates.Contains(t.Code)))
            db.NotificationTemplates.Add(NotificationTemplate.Create(code, name, subject, body, channel, priority, now));

        var projects = (await db.ProjectTenants.Select(p => p.ProjectCode).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var (code, name, description, environment, emails, chats, storage) in PlatformDefaults.Projects.Where(p => !projects.Contains(p.Code)))
            db.ProjectTenants.Add(ProjectTenant.Create(code, name, description, environment, emails, chats, storage, now));

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
    }
}
