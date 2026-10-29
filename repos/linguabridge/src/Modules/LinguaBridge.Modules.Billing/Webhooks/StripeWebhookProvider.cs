using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinguaBridge.Application.Abstractions;

namespace LinguaBridge.Modules.Billing.Webhooks;

/// <summary>
/// Stripe. The <c>Stripe-Signature</c> header is <c>t=timestamp,v1=signature[,v1=...]</c>, where a signature is
/// HMAC-SHA256 (hex) of <c>"{t}.{body}"</c>. A timestamp further than the tolerance from now is refused, so a captured
/// request cannot be replayed later. A payment is a <c>checkout.session.completed</c> (or
/// <c>checkout.session.async_payment_succeeded</c>) event whose session is paid; the payer and plan come from
/// <c>client_reference_id</c> (<see cref="OrderReference"/>), and <c>amount_total</c> in <c>vnd</c> (a zero-decimal
/// currency, so already whole dong) is what was paid.
/// </summary>
public sealed class StripeWebhookProvider(WebhookOptions options, IDateTimeProvider clock) : IWebhookProvider
{
    public const string SignatureHeader = "Stripe-Signature";

    public string Name => "Stripe";

    public Result<WebhookNotification> Verify(WebhookRequest request)
    {
        if (string.IsNullOrEmpty(options.StripeSecretKey))
            return WebhookErrors.NotConfigured(Name);

        if (!request.Headers.TryGetValue(SignatureHeader, out var header) || !SignatureMatches(request.Body, header))
            return WebhookErrors.SignatureInvalid();

        try
        {
            using var document = JsonDocument.Parse(request.Body);
            return Read(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return WebhookErrors.PayloadInvalid("The body is not a Stripe event.");
        }
    }

    /// <summary>Builds the header Stripe would send; public so a test (or a replay tool) can sign a message the same way.</summary>
    public static string Sign(string body, string secretKey, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={timestamp},v1={ComputeSignature(timestamp, body, secretKey)}";
    }

    private Result<WebhookNotification> Read(JsonElement root)
    {
        var eventId = root.GetProperty("id").GetString();
        var type = root.GetProperty("type").GetString();
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(type))
            return WebhookErrors.PayloadInvalid("The event has no id or type.");

        if (type is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded"))
            return new IgnoredNotification($"Event type '{type}' does not change a subscription.");

        var session = root.GetProperty("data").GetProperty("object");
        if (session.TryGetProperty("payment_status", out var status) && status.GetString() != "paid")
            return new IgnoredNotification("The checkout session is not paid yet.");

        var reference = session.TryGetProperty("client_reference_id", out var value) ? value.GetString() : null;
        if (!OrderReference.TryParse(reference, out var userId, out var planId))
            return WebhookErrors.PayloadInvalid("client_reference_id does not name a learner and a plan.");

        decimal? amountVnd = null;
        if (session.TryGetProperty("amount_total", out var total) && total.TryGetInt64(out var amount)
            && session.TryGetProperty("currency", out var currency) && string.Equals(currency.GetString(), "vnd", StringComparison.OrdinalIgnoreCase))
        {
            amountVnd = amount;
        }

        return new PaymentConfirmed(eventId, userId, planId, amountVnd);
    }

    private bool SignatureMatches(string body, string header)
    {
        string? timestamp = null;
        var candidates = new List<string>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
                continue;

            var name = pair[0].Trim();
            if (name == "t")
                timestamp = pair[1].Trim();
            else if (name == "v1")
                candidates.Add(pair[1].Trim());
        }

        if (timestamp is null || candidates.Count == 0 || !long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return false;

        var age = Math.Abs((clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds)).TotalSeconds);
        if (age > options.StripeToleranceSeconds)
            return false;

        var expected = Convert.FromHexString(ComputeSignature(timestamp, body, options.StripeSecretKey));
        return candidates.Any(candidate => TryFromHex(candidate) is { } bytes && CryptographicOperations.FixedTimeEquals(bytes, expected));
    }

    private static string ComputeSignature(string timestamp, string body, string secretKey) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

    private static byte[]? TryFromHex(string hex)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
