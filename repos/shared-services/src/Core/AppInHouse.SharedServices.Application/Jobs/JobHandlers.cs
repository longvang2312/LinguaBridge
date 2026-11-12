using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Jobs;

// One handler per queue. A handler that throws makes the job wait and try again (1 s, 5 s, 15 s by default); a handler that knows trying
// again cannot help (no configuration, a refused request, a deactivated project, an exhausted quota) throws JobPermanentFailureException.

public sealed record EmailJobPayload(string Project, string To, string Subject, string HtmlBody, string TextBody);

public sealed record ChatJobPayload(string Project, string? Recipient, string Text);

public sealed record ThumbnailJobPayload(Guid FileId);

public sealed class EmailJobHandler(IEmailDispatcher dispatcher) : IJobHandler
{
    public string Queue => JobQueues.Email;

    public async Task HandleAsync(BackgroundJobRecord job, CancellationToken cancellationToken)
    {
        var payload = JobJson.Read<EmailJobPayload>(job);
        SendEmailResult result;
        try
        {
            result = await dispatcher.SendAsync(new SendEmailCommand(payload.Project, payload.To, Subject: payload.Subject, HtmlBody: payload.HtmlBody, TextBody: payload.TextBody), cancellationToken);
        }
        catch (Exception ex) when (ex is QuotaExceededException or ProjectInactiveException or ValidationException)
        {
            throw new JobPermanentFailureException(ex.Message);
        }

        if (result.Failure == EmailFailure.NoConfiguration) throw new JobPermanentFailureException(result.Error ?? "There is no email configuration.");
        if (result.Failure != EmailFailure.None) throw new InvalidOperationException(result.Error ?? "The email could not be delivered.");
    }
}

public sealed class ChatJobHandler(INotificationBotDispatcher dispatcher, IOfflineNotificationHandler? offline = null) : IJobHandler
{
    public string Queue => JobQueues.Chat;

    public async Task HandleAsync(BackgroundJobRecord job, CancellationToken cancellationToken)
    {
        // The queue also carries the notices to conversation members who stayed offline (a job type of their own).
        if (job.Type == ConversationService.OfflineJobType)
        {
            if (offline is null) throw new JobPermanentFailureException("Offline notifications are not available.");
            await offline.HandleAsync(JobJson.Read<OfflineNotificationPayload>(job), cancellationToken);
            return;
        }

        var payload = JobJson.Read<ChatJobPayload>(job);
        SendBotMessageResult result;
        try
        {
            result = await dispatcher.SendAsync(new SendBotMessageCommand(payload.Project, payload.Text, payload.Recipient, BotFormat.PlainText), cancellationToken);
        }
        catch (Exception ex) when (ex is QuotaExceededException or ProjectInactiveException or ValidationException)
        {
            throw new JobPermanentFailureException(ex.Message);
        }

        if (result.Failure == BotFailure.NoConfiguration) throw new JobPermanentFailureException(result.Error ?? "There is no chat configuration.");
        if (result.Failure != BotFailure.None) throw new InvalidOperationException(result.Error ?? "The message could not be delivered.");
    }
}

public sealed class MediaThumbnailJobHandler(IThumbnailRebuilder rebuilder) : IJobHandler
{
    public string Queue => JobQueues.MediaThumbnail;

    public async Task HandleAsync(BackgroundJobRecord job, CancellationToken cancellationToken)
    {
        var payload = JobJson.Read<ThumbnailJobPayload>(job);
        var result = await rebuilder.RebuildAsync(payload.FileId, cancellationToken);
        switch (result.Outcome)
        {
            case ThumbnailRebuildOutcome.Rebuilt:
                return;
            case ThumbnailRebuildOutcome.NotFound:
            case ThumbnailRebuildOutcome.NotAnImage:
                throw new JobPermanentFailureException(result.Error ?? "There is nothing to rebuild.");
            default:
                throw new InvalidOperationException(result.Error ?? "The thumbnail could not be rebuilt.");
        }
    }
}

/// <summary>Turns one published event into one <c>webhook</c> job per subscription, so a slow application never holds up the others.</summary>
public sealed class OutboxJobHandler(IWebhookRepository subscriptions, IJobQueue jobs) : IJobHandler
{
    public string Queue => JobQueues.Outbox;

    public async Task HandleAsync(BackgroundJobRecord job, CancellationToken cancellationToken)
    {
        var payload = JobJson.Read<OutboxEventPayload>(job);
        foreach (var subscription in await subscriptions.ListActiveForEventAsync(payload.Event, payload.Project, cancellationToken))
            await jobs.EnqueueAsync(JobQueues.Webhook, "deliver", new WebhookDeliveryPayload(subscription.Id, payload.EventId, payload.Event, payload.Body), cancellationToken);
    }
}

/// <summary>Two kinds of webhook work: <c>deliver</c> (post an event to a subscription) and <c>inbound</c> (process a call that arrived).</summary>
public sealed class WebhookJobHandler(
    IWebhookRepository webhooks,
    IWebhookDeliverer deliverer,
    IEventPublisher events,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IJobHandler
{
    public string Queue => JobQueues.Webhook;

    public Task HandleAsync(BackgroundJobRecord job, CancellationToken cancellationToken) => job.Type switch
    {
        "deliver" => DeliverAsync(job, cancellationToken),
        "inbound" => ProcessInboundAsync(job, cancellationToken),
        _ => throw new JobPermanentFailureException($"Unknown webhook job type '{job.Type}'."),
    };

    private async Task DeliverAsync(BackgroundJobRecord job, CancellationToken cancellationToken)
    {
        var payload = JobJson.Read<WebhookDeliveryPayload>(job);
        var subscription = await webhooks.GetAsync(payload.SubscriptionId, cancellationToken);

        // Deleted or switched off since the event happened: nothing to do, and nothing to retry.
        if (subscription is null || !subscription.IsActive) return;

        var result = await deliverer.DeliverAsync(subscription, payload.Event, payload.EventId, payload.Body, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);   // the delivery counters

        if (result.Success) return;
        var error = result.Error ?? "The delivery failed.";
        throw result.Permanent ? new JobPermanentFailureException(error) : new InvalidOperationException(error);
    }

    private async Task ProcessInboundAsync(BackgroundJobRecord job, CancellationToken cancellationToken)
    {
        var payload = JobJson.Read<InboundWebhookPayload>(job);
        var log = await webhooks.GetInboundAsync(payload.LogId, cancellationToken) ?? throw new JobPermanentFailureException("The inbound call is no longer stored.");

        // What arrived is handed on to the applications that subscribed to "webhook.received"; the call itself is then done.
        await events.PublishAsync(WebhookEvents.WebhookReceived, ProjectTenant.PlatformCode, new { source = log.Source, logId = log.Id, payload = log.Payload }, cancellationToken);
        log.MarkProcessed(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

// ------------------------------------------------------------------------------------------------ thumbnails

public enum ThumbnailRebuildOutcome
{
    Rebuilt,
    NotFound,
    NotAnImage,
    Failed,
}

public sealed record ThumbnailRebuildResult(ThumbnailRebuildOutcome Outcome, string? Error = null);

/// <summary>Makes the thumbnail of a stored image again from the original (for a file whose thumbnail is missing or broken).</summary>
public interface IThumbnailRebuilder
{
    Task<ThumbnailRebuildResult> RebuildAsync(Guid fileId, CancellationToken cancellationToken = default);
}

public sealed class ThumbnailRebuilder(
    IChannelConfigManager configurations,
    IEnumerable<IStorageProvider> providers,
    IImageThumbnailService thumbnails,
    IStoredFileRepository files,
    IMediaLogRepository mediaLogs,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ThumbnailRebuilder> logger) : IThumbnailRebuilder
{
    public async Task<ThumbnailRebuildResult> RebuildAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await files.GetByIdAsync(fileId, cancellationToken);
        if (file is null) return new ThumbnailRebuildResult(ThumbnailRebuildOutcome.NotFound, "The file no longer exists.");
        if (!file.CanHaveThumbnail) return new ThumbnailRebuildResult(ThumbnailRebuildOutcome.NotAnImage, "Only an image has a thumbnail.");

        // Like a download or a delete, this uses the storage the file was written to, not the one that is active now.
        ChannelRuntimeConfiguration? configuration;
        try
        {
            configuration = await configurations.GetForRuntimeAsync(file.StorageProfileId, cancellationToken);
        }
        catch (SecretProtectionException)
        {
            return await Failed(file, "The stored storage credentials cannot be read; enter them again.", cancellationToken);
        }

        var provider = providers.FirstOrDefault(p => p.Type == file.StorageProvider);
        if (configuration is null || provider is null) return await Failed(file, "The storage the file was written to is not available.", cancellationToken);

        try
        {
            await using var original = await provider.OpenReadAsync(configuration, file.ObjectKey, cancellationToken);
            if (original is null) return await Failed(file, "The file is no longer in the storage.", cancellationToken);

            using var buffer = new MemoryStream();
            await original.CopyToAsync(buffer, cancellationToken);
            var thumbnail = await thumbnails.CreateThumbnailAsync(buffer.ToArray(), file.MimeType, cancellationToken);
            await provider.PutAsync(configuration, file.ThumbnailObjectKey, new MemoryStream(thumbnail, writable: false), "image/webp", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ImageRejectedException ex)
        {
            return await Failed(file, ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The thumbnail of file {FileId} could not be rebuilt.", file.Id);
            return await Failed(file, ErrorText.Clean(ex.Message, configuration.Secret), cancellationToken);
        }

        mediaLogs.Add(MediaLog.Create(file.ProjectCode, file.Id, "Thumbnail", "Success", null, clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ThumbnailRebuildResult(ThumbnailRebuildOutcome.Rebuilt);
    }

    private async Task<ThumbnailRebuildResult> Failed(StoredFile file, string error, CancellationToken cancellationToken)
    {
        mediaLogs.Add(MediaLog.Create(file.ProjectCode, file.Id, "Thumbnail", "Failed", ErrorText.Clean(error), clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ThumbnailRebuildResult(ThumbnailRebuildOutcome.Failed, ErrorText.Clean(error));
    }
}
