using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Webhooks;

/// <summary>
/// Signs what the service posts to an application, and checks the signature of what an application posts back. The signature is
/// <c>sha256=</c> and the lower-case hex HMAC-SHA256 of the exact bytes of the body, in the <c>X-AIH-Signature</c> header: the receiver
/// recomputes it from the raw body it got and compares in constant time.
/// </summary>
public static class WebhookSigner
{
    public const string SignatureHeader = "X-AIH-Signature";
    public const string EventHeader = "X-AIH-Event";
    public const string DeliveryHeader = "X-AIH-Delivery";
    public const string TimestampHeader = "X-AIH-Timestamp";
    private const string Prefix = "sha256=";

    public static string Sign(string secret, ReadOnlySpan<byte> body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return Prefix + Convert.ToHexStringLower(hash);
    }

    public static string Sign(string secret, string body) => Sign(secret, Encoding.UTF8.GetBytes(body));

    /// <summary>Whether <paramref name="signatureHeader"/> is the signature of <paramref name="body"/> under <paramref name="secret"/>. Constant time; any malformed value is simply false.</summary>
    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || signatureHeader is null) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, body));
        var given = Encoding.ASCII.GetBytes(signatureHeader.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}

// ------------------------------------------------------------------------------------------------ outbound

public sealed record WebhookHttpResult(int? StatusCode, string? Error);

/// <summary>Posts to an application. The implementation refuses addresses the service must not contact and follows no redirect.</summary>
public interface IWebhookSender
{
    /// <summary>Null when the address may be contacted, otherwise why not.</summary>
    Task<string?> CheckTargetAsync(string url, CancellationToken cancellationToken);

    Task<WebhookHttpResult> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}

/// <summary><see cref="Permanent"/> is set when trying again cannot help (the address is not allowed, the application answered 4xx).</summary>
public sealed record WebhookDeliveryResult(bool Success, int? StatusCode, string? Error, int LatencyMs, bool Permanent);

public interface IWebhookDeliverer
{
    /// <summary>Signs and posts <paramref name="body"/>, and records the result on the subscription (the caller saves).</summary>
    Task<WebhookDeliveryResult> DeliverAsync(WebhookSubscription subscription, string eventName, string deliveryId, string body, CancellationToken cancellationToken);
}

public sealed class WebhookDeliverer(IWebhookSender sender, ISecretProtector protector, TimeProvider clock, ILogger<WebhookDeliverer> logger) : IWebhookDeliverer
{
    public async Task<WebhookDeliveryResult> DeliverAsync(
        WebhookSubscription subscription, string eventName, string deliveryId, string body, CancellationToken cancellationToken)
    {
        string secret;
        try
        {
            secret = protector.Unprotect(subscription.ProtectedSecret);
        }
        catch (SecretProtectionException)
        {
            return Record(subscription, new WebhookDeliveryResult(false, null, "The stored signing key cannot be read; create the subscription again.", 0, true));
        }

        var blocked = await sender.CheckTargetAsync(subscription.TargetUrl, cancellationToken);
        if (blocked is not null) return Record(subscription, new WebhookDeliveryResult(false, null, ErrorText.Clean(blocked), 0, true));

        var headers = new Dictionary<string, string>
        {
            [WebhookSigner.SignatureHeader] = WebhookSigner.Sign(secret, body),
            [WebhookSigner.EventHeader] = eventName,
            [WebhookSigner.DeliveryHeader] = deliveryId,
            [WebhookSigner.TimestampHeader] = clock.GetUtcNow().ToUnixTimeSeconds().ToString(),
        };

        var stopwatch = Stopwatch.StartNew();
        WebhookHttpResult http;
        try
        {
            http = await sender.PostAsync(subscription.TargetUrl, body, headers, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Posting to webhook subscription {Subscription} threw.", subscription.Id);
            http = new WebhookHttpResult(null, ErrorText.Clean(ex.Message, secret));
        }

        stopwatch.Stop();
        var latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        var result = http.StatusCode is >= 200 and < 300
            ? new WebhookDeliveryResult(true, http.StatusCode, null, latency, false)
            : new WebhookDeliveryResult(
                false, http.StatusCode, http.Error ?? $"The application answered {http.StatusCode}.", latency,
                Permanent: http.StatusCode is >= 400 and < 500 and not (408 or 429));
        return Record(subscription, result);
    }

    private WebhookDeliveryResult Record(WebhookSubscription subscription, WebhookDeliveryResult result)
    {
        subscription.RecordDelivery(result.Success, result.StatusCode?.ToString() ?? result.Error ?? "failed", clock.GetUtcNow());
        return result;
    }
}

// ------------------------------------------------------------------------------------------------ subscriptions

public sealed record CreateWebhookSubscriptionCommand(string ProjectCode, string Name, string TargetUrl, IReadOnlyList<string> Events, string? SecretKey = null);

public sealed record WebhookSubscriptionDto(
    Guid Id, string ProjectCode, string Name, string TargetUrl, string SecretMask, IReadOnlyList<string> Events, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset? LastDeliveryAt, string? LastStatus, int SuccessCount, int FailureCount, int? SuccessRatePercent);

/// <summary>The subscription and its signing key, shown once. <see cref="ToString"/> hides the key, so logging the object cannot leak it.</summary>
public sealed record CreatedWebhookSubscriptionDto(WebhookSubscriptionDto Subscription, string SecretKey)
{
    public override string ToString() => $"CreatedWebhookSubscriptionDto {{ Subscription = {Subscription.Id}, SecretKey = ••••{SecretKey[^4..]} }}";
}

public sealed record WebhookTestResult(bool Success, int? StatusCode, string? Error, int LatencyMs);

public interface IWebhookManager
{
    Task<IReadOnlyList<WebhookSubscriptionDto>> ListAsync(string? projectCode, CancellationToken cancellationToken = default);

    /// <exception cref="NotFoundException">There is no such subscription.</exception>
    Task<WebhookSubscriptionDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CreatedWebhookSubscriptionDto> CreateAsync(CreateWebhookSubscriptionCommand command, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Posts a <c>webhook.test</c> event to the subscription now and says what the application answered.</summary>
    Task<WebhookTestResult> TestAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class WebhookManager(
    IWebhookRepository subscriptions,
    ISecretProtector protector,
    IWebhookSender sender,
    IWebhookDeliverer deliverer,
    IUnitOfWork unitOfWork,
    IAuditLogWriter audit,
    TimeProvider clock) : IWebhookManager
{
    public async Task<IReadOnlyList<WebhookSubscriptionDto>> ListAsync(string? projectCode, CancellationToken cancellationToken = default)
    {
        var project = string.IsNullOrWhiteSpace(projectCode) ? null : ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        return [.. (await subscriptions.ListAsync(project, cancellationToken)).Select(ToDto)];
    }

    public async Task<WebhookSubscriptionDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await subscriptions.GetAsync(id, cancellationToken) ?? throw new NotFoundException("There is no such webhook subscription."));

    public async Task<CreatedWebhookSubscriptionDto> CreateAsync(CreateWebhookSubscriptionCommand command, CancellationToken cancellationToken = default)
    {
        var project = ChannelConfigurationValidator.NormalizeProjectCode(command.ProjectCode);
        if (!ChannelConfigurationValidator.IsValidProjectCode(project))
            throw Invalid("ProjectCode", "Use 1 to 50 lower-case letters, digits, '-' or '_', starting with a letter or digit.");

        var secret = string.IsNullOrWhiteSpace(command.SecretKey) ? NewSecret() : command.SecretKey;
        WebhookSubscription subscription;
        try
        {
            subscription = WebhookSubscription.Create(project, command.Name, command.TargetUrl?.Trim() ?? string.Empty, protector.Protect(secret), secret, command.Events ?? [], clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            throw Invalid("Subscription", ex.Message);
        }

        var blocked = await sender.CheckTargetAsync(subscription.TargetUrl, cancellationToken);
        if (blocked is not null) throw Invalid("TargetUrl", ErrorText.Clean(blocked));

        subscriptions.Add(subscription);
        // The address can carry a token in its query, so only the host is audited.
        audit.Record("CreateWebhookSubscription", "WebhookSubscription", subscription.Id.ToString(),
            new { subscription.ProjectCode, subscription.Name, Host = new Uri(subscription.TargetUrl).Host, Events = subscription.EventList });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CreatedWebhookSubscriptionDto(ToDto(subscription), secret);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await subscriptions.GetAsync(id, cancellationToken) ?? throw new NotFoundException("There is no such webhook subscription.");
        subscriptions.Remove(subscription);
        audit.Record("DeleteWebhookSubscription", "WebhookSubscription", id.ToString(), new { subscription.ProjectCode, subscription.Name });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<WebhookTestResult> TestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await subscriptions.GetAsync(id, cancellationToken) ?? throw new NotFoundException("There is no such webhook subscription.");
        var deliveryId = Guid.NewGuid().ToString();
        var body = WebhookEnvelope.Build(deliveryId, WebhookEvents.Test, subscription.ProjectCode, clock.GetUtcNow(), new { message = "This is a test delivery from AppInHouse Shared Services." });

        var result = await deliverer.DeliverAsync(subscription, WebhookEvents.Test, deliveryId, body, cancellationToken);
        audit.Record("TestWebhookSubscription", "WebhookSubscription", id.ToString(), new { result.Success, result.StatusCode });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new WebhookTestResult(result.Success, result.StatusCode, result.Error, result.LatencyMs);
    }

    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });

    internal static WebhookSubscriptionDto ToDto(WebhookSubscription s)
    {
        var total = s.SuccessCount + s.FailureCount;
        return new WebhookSubscriptionDto(
            s.Id, s.ProjectCode, s.Name, s.TargetUrl, "••••••••" + s.SecretHint, s.EventList, s.IsActive, s.CreatedAt,
            s.LastDeliveryAt, s.LastStatus, s.SuccessCount, s.FailureCount, total == 0 ? null : (int)Math.Round(100.0 * s.SuccessCount / total));
    }
}

/// <summary>The body every outbound webhook has. It is built once and signed as it is, so the receiver can verify exactly what it was sent.</summary>
public static class WebhookEnvelope
{
    public static string Build(string id, string eventName, string projectCode, DateTimeOffset occurredAt, object data) =>
        JsonSerializer.Serialize(new { id, @event = eventName, project = projectCode, occurredAt, data }, JobJson.Options);
}

// ------------------------------------------------------------------------------------------------ events and the outbox

/// <summary>
/// Publishes an event as a job in the <c>outbox</c> queue, but only when somebody subscribed to it. The outbox job turns it into one
/// <c>webhook</c> job per subscription, so each application is retried on its own.
/// </summary>
public sealed class OutboxEventPublisher(IWebhookRepository subscriptions, IJobQueue jobs, TimeProvider clock, ILogger<OutboxEventPublisher> logger) : IEventPublisher
{
    public async Task PublishAsync(string eventName, string projectCode, object data, CancellationToken cancellationToken = default)
    {
        try
        {
            var interested = await subscriptions.ListActiveForEventAsync(eventName, projectCode, cancellationToken);
            if (interested.Count == 0) return;

            var id = Guid.NewGuid().ToString();
            var body = WebhookEnvelope.Build(id, eventName, projectCode, clock.GetUtcNow(), data);
            await jobs.EnqueueAsync(JobQueues.Outbox, "event", new OutboxEventPayload(id, eventName, projectCode, body), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The email was sent, the file was stored: that stands even if nobody can be told about it.
            logger.LogWarning(ex, "The event {Event} of project {Project} could not be published.", eventName, projectCode);
        }
    }
}

public sealed record OutboxEventPayload(string EventId, string Event, string Project, string Body);

public sealed record WebhookDeliveryPayload(Guid SubscriptionId, string EventId, string Event, string Body);

public sealed record InboundWebhookPayload(Guid LogId);

// ------------------------------------------------------------------------------------------------ inbound

public enum InboundOutcome
{
    Accepted,
    UnknownSource,

    /// <summary>The source is known but this installation has no secret for it, so nothing it sends can be trusted.</summary>
    NotConfigured,
    InvalidSignature,
    TooLarge,
}

public sealed record InboundResult(InboundOutcome Outcome, Guid? LogId);

public sealed record InboundSourceDto(
    string Source, string Path, string Verification, string Description, bool Configured, int Received24h, int Rejected24h, DateTimeOffset? LastReceivedAt);

/// <summary>The secret that proves a call comes from a source (the Telegram secret token, the GitHub or Stripe signing secret); from host configuration.</summary>
public interface IInboundSecretProvider
{
    string? GetSecret(string source);
}

public interface IInboundWebhookReceiver
{
    /// <param name="headers">Case-insensitive.</param>
    Task<InboundResult> ReceiveAsync(string source, IReadOnlyDictionary<string, string> headers, byte[] body, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InboundSourceDto>> GetSourcesAsync(CancellationToken cancellationToken = default);
}

public static class InboundSources
{
    public const int MaxBodyBytes = 256 * 1024;
    public const string Telegram = "telegram";
    public const string GitHub = "github";
    public const string Stripe = "stripe";

    /// <summary>How far a Stripe timestamp may be from now before the call is taken for a replay.</summary>
    public static readonly TimeSpan StripeTolerance = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<(string Source, string Verification, string Description)> All { get; } =
    [
        (Telegram, "Secret token in the X-Telegram-Bot-Api-Secret-Token header", "Updates from the Telegram Bot API (messages, commands, button presses)."),
        (GitHub, "HMAC SHA-256 of the body in the X-Hub-Signature-256 header", "Events from GitHub webhooks (pushes, releases, workflow runs)."),
        (Stripe, "Stripe-Signature header (timestamp and v1 HMAC SHA-256, 5 minutes tolerance)", "Payment events from Stripe."),
    ];

    /// <summary>Null when the call is genuine, otherwise why it is not. Everything is compared in constant time.</summary>
    public static string? Verify(string source, string secret, IReadOnlyDictionary<string, string> headers, byte[] body, DateTimeOffset now)
    {
        string? Header(string name) => headers.TryGetValue(name, out var value) ? value : null;

        switch (source)
        {
            case Telegram:
                var token = Header("X-Telegram-Bot-Api-Secret-Token");
                return token is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(secret))
                    ? null : "The secret token is missing or wrong.";

            case GitHub:
                return WebhookSigner.IsValid(secret, body, Header("X-Hub-Signature-256")) ? null : "The signature is missing or wrong.";

            case Stripe:
                var header = Header("Stripe-Signature");
                if (header is null) return "The signature is missing.";
                string? timestamp = null;
                var signatures = new List<string>();
                foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length != 2) continue;
                    if (pair[0] == "t") timestamp = pair[1];
                    else if (pair[0] == "v1") signatures.Add(pair[1]);
                }

                if (!long.TryParse(timestamp, out var seconds) || signatures.Count == 0) return "The signature is malformed.";
                if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > StripeTolerance) return "The timestamp is too old or too far ahead.";

                var signed = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
                var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed)));
                return signatures.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s))) ? null : "The signature is wrong.";

            default:
                return "Unknown source.";
        }
    }
}

public sealed class InboundWebhookReceiver(
    IWebhookRepository webhooks,
    IUsageRepository usage,
    IInboundSecretProvider secrets,
    IJobQueue jobs,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IInboundWebhookReceiver
{
    public async Task<InboundResult> ReceiveAsync(string source, IReadOnlyDictionary<string, string> headers, byte[] body, CancellationToken cancellationToken = default)
    {
        var name = (source ?? string.Empty).Trim().ToLowerInvariant();
        if (!InboundSources.All.Any(s => s.Source == name)) return new InboundResult(InboundOutcome.UnknownSource, null);

        var now = clock.GetUtcNow();
        if (body.Length > InboundSources.MaxBodyBytes) return await Reject(name, InboundOutcome.TooLarge, "The body is too large.", body.Length, now, cancellationToken);

        var secret = secrets.GetSecret(name);
        if (string.IsNullOrEmpty(secret)) return await Reject(name, InboundOutcome.NotConfigured, "No secret is configured for this source.", body.Length, now, cancellationToken);

        var problem = InboundSources.Verify(name, secret, headers, body, now);
        if (problem is not null) return await Reject(name, InboundOutcome.InvalidSignature, problem, body.Length, now, cancellationToken);

        var log = InboundWebhookLog.Accepted(name, Encoding.UTF8.GetString(body), body.Length, now);
        webhooks.AddInbound(log);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await jobs.EnqueueAsync(JobQueues.Webhook, "inbound", new InboundWebhookPayload(log.Id), cancellationToken);
        return new InboundResult(InboundOutcome.Accepted, log.Id);
    }

    public async Task<IReadOnlyList<InboundSourceDto>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        var since = clock.GetUtcNow().AddHours(-24);
        var result = new List<InboundSourceDto>();
        foreach (var (source, verification, description) in InboundSources.All)
        {
            result.Add(new InboundSourceDto(
                source, $"/api/v1/webhooks/incoming/{source}", verification, description, !string.IsNullOrEmpty(secrets.GetSecret(source)),
                await usage.CountInboundAsync(source, InboundWebhookStatus.Received, since, cancellationToken)
                + await usage.CountInboundAsync(source, InboundWebhookStatus.Processed, since, cancellationToken)
                + await usage.CountInboundAsync(source, InboundWebhookStatus.Failed, since, cancellationToken),
                await usage.CountInboundAsync(source, InboundWebhookStatus.Rejected, since, cancellationToken),
                await usage.LastInboundAsync(source, cancellationToken)));
        }

        return result;
    }

    private async Task<InboundResult> Reject(string source, InboundOutcome outcome, string reason, int size, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var log = InboundWebhookLog.Rejected(source, reason, size, now);
        webhooks.AddInbound(log);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new InboundResult(outcome, log.Id);
    }
}
