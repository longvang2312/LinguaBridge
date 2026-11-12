using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Jobs;

public static class JobJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>Reads a job's payload; one that cannot be read will never be readable, so it is a permanent failure.</summary>
    public static T Read<T>(BackgroundJobRecord job)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(job.PayloadJson, Options) ?? throw new JobPermanentFailureException("The job has no payload.");
        }
        catch (JsonException)
        {
            throw new JobPermanentFailureException("The job's payload cannot be read.");
        }
    }
}

/// <summary>
/// The queues. A job is a row (so it survives a restart and can be counted and retried) plus an item in the in-memory channel of its queue
/// that wakes the workers. Saving comes first: an item for a job that was not saved can never exist.
/// </summary>
public sealed class JobQueueService(
    IBackgroundJobRepository jobs,
    IJobChannels channels,
    IUnitOfWork unitOfWork,
    IAuditLogWriter audit,
    JobSettings settings,
    TimeProvider clock) : IJobQueue
{
    private static readonly IReadOnlyDictionary<string, string> Purposes = new Dictionary<string, string>
    {
        [JobQueues.Email] = "Email sent in the background (retried when the provider is down)",
        [JobQueues.Chat] = "Chat messages sent in the background (Telegram, Discord, Slack)",
        [JobQueues.MediaThumbnail] = "Thumbnails rebuilt for stored images",
        [JobQueues.Outbox] = "Events waiting to be turned into webhook deliveries",
        [JobQueues.Webhook] = "Outbound webhook deliveries and inbound webhook processing",
    };

    public async Task<Guid> EnqueueAsync(string queue, string type, object payload, CancellationToken cancellationToken = default, TimeSpan? delay = null)
    {
        var job = BackgroundJobRecord.Create(queue, type, JobJson.Serialize(payload), settings.MaxAttempts, clock.GetUtcNow(), delay);
        jobs.Add(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        channels.Write(new BackgroundJobItem(job.Id, job.Queue));
        return job.Id;
    }

    public async Task<IReadOnlyList<JobQueueStatusDto>> GetQueuesAsync(CancellationToken cancellationToken = default)
    {
        var counts = (await jobs.CountsAsync(clock.GetUtcNow().AddHours(-24), cancellationToken)).ToDictionary(c => c.Queue);
        return [.. JobQueues.All.Select(name =>
        {
            var c = counts.GetValueOrDefault(name) ?? new QueueCounts(name, 0, 0, 0, 0);
            return new JobQueueStatusDto(name, Purposes[name], c.Pending, c.Processing, c.CompletedSince, c.Failed, c.Failed > 0 ? "Degraded" : "Healthy");
        })];
    }

    public async Task<RetryFailedResult> RetryFailedAsync(string queue, CancellationToken cancellationToken = default)
    {
        if (!JobQueues.IsKnown(queue)) throw new NotFoundException($"There is no queue '{queue}'.");

        var failed = await jobs.ListFailedAsync(queue, cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var job in failed) job.Requeue(now);

        if (failed.Count > 0)
        {
            audit.Record("RetryFailedJobs", "JobQueue", queue, new { Requeued = failed.Count });
            await unitOfWork.SaveChangesAsync(cancellationToken);
            foreach (var job in failed) channels.Write(new BackgroundJobItem(job.Id, job.Queue));
        }

        return new RetryFailedResult(queue, failed.Count);
    }
}
