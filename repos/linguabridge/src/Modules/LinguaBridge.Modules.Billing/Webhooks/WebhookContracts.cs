using System.Text.RegularExpressions;
using LinguaBridge.Application.Abstractions;

namespace LinguaBridge.Modules.Billing.Webhooks;

/// <summary>What arrived: the exact body (signatures cover the exact bytes) and the headers.</summary>
public sealed record WebhookRequest(string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>A webhook whose signature checked out, reduced to what billing needs.</summary>
public abstract record WebhookNotification;

/// <summary>The gateway says this payment succeeded.</summary>
/// <param name="EventId">The gateway's own unique id; the idempotency key together with the provider.</param>
/// <param name="AmountVnd">What was paid, in VND; null when the gateway did not say or paid in another currency.</param>
public sealed record PaymentConfirmed(string EventId, Guid UserId, string PlanId, decimal? AmountVnd) : WebhookNotification;

/// <summary>Authentic but changes nothing: an unrelated event type, or a payment that did not succeed.</summary>
public sealed record IgnoredNotification(string Reason) : WebhookNotification;

/// <summary>One payment gateway: how to prove a webhook is really from it, and how to read it.</summary>
public interface IWebhookProvider
{
    /// <summary>Canonical name, stored with the event: <c>VNPay</c>, <c>Stripe</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Checks the signature first and reads the body only if it is genuine. Fails with <c>webhook.signature_invalid</c>
    /// or <c>webhook.payload_invalid</c> (HTTP 400), or <c>webhook.not_configured</c> when no secret is set.
    /// </summary>
    Result<WebhookNotification> Verify(WebhookRequest request);
}

/// <summary>
/// How a checkout tells the webhook who paid for what. The checkout puts this string in a field the gateway echoes back
/// (VNPay <c>vnp_OrderInfo</c>, Stripe <c>client_reference_id</c>); it is trusted only because the signature covers it.
/// </summary>
public static partial class OrderReference
{
    public static string Format(Guid userId, string planId) => $"{userId:D}:{planId}";

    public static bool TryParse(string? value, out Guid userId, out string planId)
    {
        userId = Guid.Empty;
        planId = string.Empty;

        var parts = value?.Trim().Split(':');
        if (parts is not { Length: 2 } || !Guid.TryParse(parts[0], out userId) || userId == Guid.Empty || !PlanCodes.IsValid(parts[1]))
            return false;

        planId = parts[1];
        return true;
    }
}

public static partial class PlanCodes
{
    public static bool IsValid(string? code) => code is not null && PlanCodePattern().IsMatch(code);

    [GeneratedRegex("^[a-z0-9_]{1,50}$")]
    private static partial Regex PlanCodePattern();
}

internal static class WebhookErrors
{
    public static Error SignatureInvalid() => Validation("webhook.signature_invalid", "signature", "The webhook signature is missing or does not match.");

    public static Error PayloadInvalid(string message) => Validation("webhook.payload_invalid", "body", message);

    public static Error NotConfigured(string provider) =>
        new(ErrorType.Conflict, "webhook.not_configured", $"Webhooks from {provider} are not configured on this server.");

    private static Error Validation(string code, string field, string message) =>
        new(ErrorType.Validation, code, message, new Dictionary<string, string[]> { [field] = [message] });
}
