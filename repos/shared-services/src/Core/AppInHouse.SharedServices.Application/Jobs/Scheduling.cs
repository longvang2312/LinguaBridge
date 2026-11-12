using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Jobs;

/// <summary>
/// The two kinds of cron expression the service uses, in UTC: every N minutes (<c>*/5 * * * *</c>) and at a fixed time of the hour or the day
/// (<c>30 2 * * *</c>, <c>15 * * * *</c>). The day, month and weekday fields must be <c>*</c>. Anything else is refused when the task is
/// registered, not silently misread.
/// </summary>
public sealed class CronSchedule
{
    private readonly int? _everyMinutes;
    private readonly int? _minute;
    private readonly int? _hour;

    private CronSchedule(string expression, int? everyMinutes, int? minute, int? hour)
    {
        Expression = expression;
        _everyMinutes = everyMinutes;
        _minute = minute;
        _hour = hour;
    }

    public string Expression { get; }

    public static CronSchedule Parse(string expression)
    {
        expression ??= string.Empty;
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 || fields[2] != "*" || fields[3] != "*" || fields[4] != "*")
            throw new FormatException($"'{expression}' is not supported: use five fields with * for the day, month and weekday.");

        if (fields[0].StartsWith("*/", StringComparison.Ordinal))
        {
            if (fields[1] != "*" || !int.TryParse(fields[0][2..], out var every) || every is < 1 or > 59)
                throw new FormatException($"'{expression}' is not supported: use */N (1 to 59) minutes with * for the hour.");
            return new CronSchedule(expression, every, null, null);
        }

        if (!int.TryParse(fields[0], out var minute) || minute is < 0 or > 59) throw new FormatException($"'{expression}': the minute must be 0 to 59.");
        if (fields[1] == "*") return new CronSchedule(expression, null, minute, null);
        if (!int.TryParse(fields[1], out var hour) || hour is < 0 or > 23) throw new FormatException($"'{expression}': the hour must be 0 to 23 or *.");
        return new CronSchedule(expression, null, minute, hour);
    }

    /// <summary>The first matching minute strictly after <paramref name="after"/>.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset after)
    {
        var utc = after.UtcDateTime;
        var candidate = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        for (var i = 0; i < 2 * 24 * 60; i++, candidate = candidate.AddMinutes(1))
        {
            if (_everyMinutes is { } n ? candidate.Minute % n == 0 : candidate.Minute == _minute && (_hour is null || candidate.Hour == _hour))
                return new DateTimeOffset(candidate, TimeSpan.Zero);
        }

        throw new InvalidOperationException("The schedule never matches.");
    }
}

/// <summary>What the scheduler remembers about each task between runs (it starts empty at every start of the service).</summary>
public sealed class ScheduleState
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = [];

    public sealed class Entry
    {
        public DateTimeOffset NextRunAt { get; set; }
        public DateTimeOffset? LastRunAt { get; set; }
        public string? LastStatus { get; set; }
        public string? LastError { get; set; }
        public int Runs { get; set; }
        public bool IsRunning { get; set; }
    }

    public T With<T>(string id, Func<Entry> create, Func<Entry, T> use)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry)) _entries[id] = entry = create();
            return use(entry);
        }
    }
}

/// <summary>Runs the tasks when they are due, and when an administrator asks. A task that fails is recorded and runs again at its next time.</summary>
public sealed class ScheduleRunner(IServiceScopeFactory scopes, ScheduleState state, TimeProvider clock, ILogger<ScheduleRunner> logger) : IScheduleService
{
    public async Task<IReadOnlyList<ScheduledTaskDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return [.. scope.ServiceProvider.GetServices<IScheduledTask>().Select(Describe)];
    }

    public async Task<ScheduledTaskDto> RunNowAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var task = scope.ServiceProvider.GetServices<IScheduledTask>().FirstOrDefault(t => t.Id == id)
                   ?? throw new NotFoundException($"There is no scheduled task '{id}'.");
        await RunAsync(task, reschedule: false, cancellationToken);
        return Describe(task);
    }

    /// <summary>Runs every task whose time has come.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        foreach (var task in scope.ServiceProvider.GetServices<IScheduledTask>())
        {
            var due = state.With(task.Id, () => NewEntry(task), e => !e.IsRunning && e.NextRunAt <= clock.GetUtcNow());
            if (due) await RunAsync(task, reschedule: true, cancellationToken);
        }
    }

    private async Task RunAsync(IScheduledTask task, bool reschedule, CancellationToken cancellationToken)
    {
        var started = state.With(task.Id, () => NewEntry(task), e =>
        {
            if (e.IsRunning) return false;
            e.IsRunning = true;
            return true;
        });
        if (!started) return;   // already running: a second run would only do the same work twice

        string status;
        string? error = null;
        try
        {
            status = "Success: " + await task.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.With(task.Id, () => NewEntry(task), e => e.IsRunning = false);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The scheduled task {Task} failed.", task.Id);
            status = "Failed";
            error = Email.ErrorText.Clean(ex.Message);
        }

        var now = clock.GetUtcNow();
        state.With(task.Id, () => NewEntry(task), e =>
        {
            e.IsRunning = false;
            e.LastRunAt = now;
            e.LastStatus = status;
            e.LastError = error;
            e.Runs++;
            if (reschedule) e.NextRunAt = CronSchedule.Parse(task.Cron).NextAfter(now);
            return 0;
        });
    }

    private ScheduleState.Entry NewEntry(IScheduledTask task) => new() { NextRunAt = CronSchedule.Parse(task.Cron).NextAfter(clock.GetUtcNow()) };

    private ScheduledTaskDto Describe(IScheduledTask task) => state.With(task.Id, () => NewEntry(task), e =>
        new ScheduledTaskDto(task.Id, task.Name, task.Description, task.Cron, e.LastRunAt, e.NextRunAt, e.LastStatus, e.LastError, e.Runs, e.IsRunning));
}

// ------------------------------------------------------------------------------------------------ the tasks

/// <summary>Deletes delivery, media, system and notification logs older than the retention period. The audit log is never deleted.</summary>
public sealed class LogRetentionTask(IRetentionRepository retention, JobSettings settings, TimeProvider clock) : IScheduledTask
{
    public string Id => "log-retention";
    public string Name => "Delete old logs (retention)";
    public string Description => $"Removes delivery, media, system and notification logs older than {settings.LogRetentionDays} days. The audit log is kept.";
    public string Cron => "0 2 * * *";

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().AddDays(-settings.LogRetentionDays);
        var removed = await retention.DeleteDeliveryLogsBeforeAsync(cutoff, cancellationToken)
                      + await retention.DeleteMediaLogsBeforeAsync(cutoff, cancellationToken)
                      + await retention.DeleteSystemLogsBeforeAsync(cutoff, cancellationToken)
                      + await retention.DeleteNotificationLogsBeforeAsync(cutoff, cancellationToken);
        return $"{removed} old log rows removed.";
    }
}

/// <summary>Clears what the engine itself leaves behind: finished jobs and the bodies of old inbound webhook calls.</summary>
public sealed class JobHousekeepingTask(IRetentionRepository retention, JobSettings settings, TimeProvider clock) : IScheduledTask
{
    public string Id => "job-housekeeping";
    public string Name => "Clean up finished jobs and inbound webhook bodies";
    public string Description => $"Removes completed jobs older than {settings.FinishedJobRetentionDays} days and inbound webhook calls older than {settings.LogRetentionDays} days.";
    public string Cron => "30 2 * * *";

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var jobs = await retention.DeleteFinishedJobsBeforeAsync(now.AddDays(-settings.FinishedJobRetentionDays), cancellationToken);
        var calls = await retention.DeleteInboundLogsBeforeAsync(now.AddDays(-settings.LogRetentionDays), cancellationToken);
        return $"{jobs} finished jobs and {calls} inbound calls removed.";
    }
}

/// <summary>Recomputes the system metrics, so a dashboard that asks within the cache time reads a figure that is at most five minutes old.</summary>
public sealed class MetricsAggregationTask(IMetricsCollector metrics) : IScheduledTask
{
    public string Id => "metrics-aggregate";
    public string Name => "Aggregate system metrics";
    public string Description => "Recomputes the 24-hour figures and the health of the eight services.";
    public string Cron => "*/5 * * * *";

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var overview = await metrics.GetOverviewAsync(refresh: true, cancellationToken);
        return $"{overview.ServicesHealthy} of {overview.ServicesTotal} services healthy.";
    }
}
