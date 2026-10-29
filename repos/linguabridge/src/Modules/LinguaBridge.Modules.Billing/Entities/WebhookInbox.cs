using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.Billing.Entities;

/// <summary>
/// A received payment-gateway webhook. The unique (Provider, EventId) pair makes processing idempotent:
/// a redelivered event fails to insert instead of being applied twice.
/// </summary>
public class WebhookInbox : AggregateRoot
{
    public const int ProviderMaxLength = 50;
    public const int EventIdMaxLength = 255;

    // Required by EF Core.
    private WebhookInbox() { }

    /// <param name="provider">E.g. <c>VNPay</c>, <c>Stripe</c>, <c>Apple</c>, <c>Google</c>.</param>
    /// <param name="eventId">The gateway's own unique transaction/event id.</param>
    /// <param name="payload">Raw JSON body as received.</param>
    public WebhookInbox(string provider, string eventId, string payload)
    {
        Provider = Guard.Required(provider, ProviderMaxLength, nameof(provider));
        EventId = Guard.Required(eventId, EventIdMaxLength, nameof(eventId));
        Payload = string.IsNullOrWhiteSpace(payload) ? throw new ArgumentException("payload must not be empty.", nameof(payload)) : payload;
        Status = WebhookStatus.Pending;
    }

    public string Provider { get; private set; } = string.Empty;
    public string EventId { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset? ProcessedAt { get; private set; }
    public WebhookStatus Status { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void MarkProcessed()
    {
        if (Status == WebhookStatus.Processed)
            throw new InvalidOperationException("The webhook has already been processed.");

        Status = WebhookStatus.Processed;
        ProcessedAt = DateTimeOffset.UtcNow;
        ErrorMessage = null;
    }

    /// <summary>Records the failure; a failed event stays eligible for retry.</summary>
    public void MarkFailed(string errorMessage)
    {
        if (Status == WebhookStatus.Processed)
            throw new InvalidOperationException("A processed webhook cannot be marked as failed.");

        Status = WebhookStatus.Failed;
        ProcessedAt = DateTimeOffset.UtcNow;
        ErrorMessage = Guard.Required(errorMessage, int.MaxValue, nameof(errorMessage));
    }
}
