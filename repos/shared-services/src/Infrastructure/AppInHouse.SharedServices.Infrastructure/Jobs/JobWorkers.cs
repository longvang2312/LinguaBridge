using System.Threading.Channels;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Infrastructure.Jobs;

/// <summary>One unbounded <see cref="Channel{T}"/> per queue. It only wakes the workers; the job table is the truth.</summary>
public sealed class JobChannels : IJobChannels
{
    private readonly Dictionary<string, Channel<BackgroundJobItem>> _channels = JobQueues.All.ToDictionary(
        queue => queue,
        _ => Channel.CreateUnbounded<BackgroundJobItem>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));

    public void Write(BackgroundJobItem item)
    {
        if (!_channels.TryGetValue(item.Queue, out var channel)) throw new ArgumentException($"Unknown queue '{item.Queue}'.", nameof(item));
        channel.Writer.TryWrite(item);   // unbounded: always succeeds
    }

    public IAsyncEnumerable<BackgroundJobItem> ReadAllAsync(string queue, CancellationToken cancellationToken) =>
        _channels[queue].Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Runs the jobs of the five queues. At start it puts every unfinished job back in its channel (a job cut off in the middle waits again).
/// Each queue has its own workers, so a slow webhook never holds up an email. A failed attempt waits 1 s, 5 s, 15 s (see
/// <see cref="JobSettings"/>) and tries again; after the last attempt, or at once for a failure that cannot be cured by waiting, the job is
/// marked Failed and stays in the table until an administrator retries it.
/// </summary>
public sealed class BackgroundJobWorker(
    IServiceScopeFactory scopes,
    IJobChannels channels,
    JobSettings settings,
    TimeProvider clock,
    ILogger<BackgroundJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // The database may not be ready (no migration yet); the workers still start and handle what arrives.
            logger.LogError(ex, "The unfinished background jobs could not be recovered.");
        }

        var workers = JobQueues.All.SelectMany(queue =>
            Enumerable.Range(0, Math.Max(1, settings.WorkersPerQueue)).Select(_ => RunQueueAsync(queue, stoppingToken)));
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunQueueAsync(string queue, CancellationToken stoppingToken)
    {
        await foreach (var item in channels.ReadAllAsync(queue, stoppingToken))
        {
            try
            {
                await ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The loop must outlive a job whose bookkeeping failed (a database hiccup); the job is picked up again at the next start.
                logger.LogError(ex, "The background job {Job} of queue {Queue} could not be processed.", item.JobId, queue);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var unfinished = await jobs.ListUnfinishedAsync(stoppingToken);
        var now = clock.GetUtcNow();
        foreach (var job in unfinished) job.ResetInterrupted(now);
        if (unfinished.Count > 0) await unitOfWork.SaveChangesAsync(stoppingToken);

        foreach (var job in unfinished)
        {
            var wait = job.NextAttemptAt - now;
            WriteAfter(new BackgroundJobItem(job.Id, job.Queue), wait, stoppingToken);
        }

        if (unfinished.Count > 0) logger.LogInformation("{Count} unfinished background jobs were put back in their queues.", unfinished.Count);
    }

    private async Task ProcessAsync(BackgroundJobItem item, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var jobs = provider.GetRequiredService<IBackgroundJobRepository>();
        var unitOfWork = provider.GetRequiredService<IUnitOfWork>();

        var claim = await jobs.ClaimAsync(item.JobId, clock.GetUtcNow(), stoppingToken);
        if (claim.Job is not { } job)
        {
            // Not pending any more (done, or taken by another worker), or not due yet: in that case look again when it is.
            if (claim.NotBefore is { } notBefore) WriteAfter(item, notBefore - clock.GetUtcNow(), stoppingToken);
            return;
        }

        var handler = provider.GetServices<IJobHandler>().FirstOrDefault(h => h.Queue == job.Queue);
        if (handler is null)
        {
            job.MarkAttemptFailed($"No handler for queue '{job.Queue}'.", clock.GetUtcNow(), clock.GetUtcNow(), permanent: true);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            return;
        }

        try
        {
            await handler.HandleAsync(job, stoppingToken);
            job.MarkCompleted(clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The service is stopping: the job goes back to waiting (it is found again at the next start).
            job.ResetInterrupted(clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (JobPermanentFailureException ex)
        {
            var now = clock.GetUtcNow();
            job.MarkAttemptFailed(ErrorText.Clean(ex.Message), now, now, permanent: true);
            logger.LogWarning("Job {Job} of queue {Queue} failed for good: {Error}", job.Id, job.Queue, job.LastError);
        }
        catch (Exception ex)
        {
            var now = clock.GetUtcNow();
            var delay = settings.DelayAfter(job.Attempts);
            var willRetry = job.MarkAttemptFailed(ErrorText.Clean(ex.Message), now, now + delay);
            logger.LogWarning(
                "Attempt {Attempt} of job {Job} (queue {Queue}) failed: {Error}. {Next}",
                job.Attempts, job.Id, job.Queue, job.LastError, willRetry ? $"Trying again in {delay.TotalSeconds:0.#} s." : "No attempts left.");
            if (willRetry) WriteAfter(item, delay, stoppingToken);
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Puts the item back in its channel after <paramref name="delay"/>. If the service stops first the job simply stays pending in the table.</summary>
    private void WriteAfter(BackgroundJobItem item, TimeSpan delay, CancellationToken stoppingToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            channels.Write(item);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
                channels.Write(item);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }
}

/// <summary>Wakes up regularly and runs the scheduled tasks whose time has come (<c>Jobs:SchedulesEnabled=false</c> switches it off; an administrator can still run a task by hand).</summary>
public sealed class ScheduleWorker(ScheduleRunner runner, JobSettings settings, TimeProvider clock, ILogger<ScheduleWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.SchedulesEnabled) return;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await runner.RunDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The scheduler could not run the tasks that are due.");
                }

                await Task.Delay(Tick, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
