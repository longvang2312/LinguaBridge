using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinguaBridge.Application.Abstractions;

namespace LinguaBridge.Modules.Billing.Webhooks;

/// <summary>
/// VNPay. The body is a flat JSON object of <c>vnp_*</c> fields. <c>vnp_SecureHash</c> is HMAC-SHA512 (hex) over the
/// other <c>vnp_*</c> fields: non-empty ones, sorted by name, as <c>name=urlencoded(value)</c> joined with <c>&amp;</c>.
/// Payment succeeded when <c>vnp_ResponseCode</c> and <c>vnp_TransactionStatus</c> are both <c>00</c>; the event id is
/// <c>vnp_TransactionNo</c>, the payer and plan come from <c>vnp_OrderInfo</c> (<see cref="OrderReference"/>), and
/// <c>vnp_Amount</c> is in VND x 100.
/// </summary>
public sealed class VnPayWebhookProvider(WebhookOptions options) : IWebhookProvider
{
    private const string HashField = "vnp_SecureHash";
    private const string HashTypeField = "vnp_SecureHashType";

    public string Name => "VNPay";

    public Result<WebhookNotification> Verify(WebhookRequest request)
    {
        if (string.IsNullOrEmpty(options.VnPaySecretKey))
            return WebhookErrors.NotConfigured(Name);

        var fields = ReadFields(request.Body);
        if (fields is null)
            return WebhookErrors.PayloadInvalid("The body must be a flat JSON object of vnp_* fields.");

        if (!fields.TryGetValue(HashField, out var provided) || !SignatureMatches(fields, provided))
            return WebhookErrors.SignatureInvalid();

        if (Get(fields, "vnp_ResponseCode") != "00" || Get(fields, "vnp_TransactionStatus") != "00")
            return new IgnoredNotification("VNPay reports that the payment did not succeed.");

        var transactionNo = Get(fields, "vnp_TransactionNo");
        if (string.IsNullOrEmpty(transactionNo) || transactionNo == "0")
            return WebhookErrors.PayloadInvalid("vnp_TransactionNo is missing.");
        if (!OrderReference.TryParse(Get(fields, "vnp_OrderInfo"), out var userId, out var planId))
            return WebhookErrors.PayloadInvalid("vnp_OrderInfo does not name a learner and a plan.");

        // VNPay sends the amount multiplied by 100.
        decimal? amountVnd = decimal.TryParse(Get(fields, "vnp_Amount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor) ? minor / 100m : null;

        return new PaymentConfirmed(transactionNo, userId, planId, amountVnd);
    }

    /// <summary>Computes the hash the way VNPay does; public so a checkout or a test can sign a message the same way.</summary>
    public static string Sign(IReadOnlyDictionary<string, string> fields, string secretKey)
    {
        var data = string.Join('&', fields
            .Where(f => f.Key.StartsWith("vnp_", StringComparison.Ordinal) && f.Key is not HashField and not HashTypeField && !string.IsNullOrEmpty(f.Value))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => $"{f.Key}={WebUtility.UrlEncode(f.Value)}"));

        return Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(data)));
    }

    private bool SignatureMatches(Dictionary<string, string> fields, string provided)
    {
        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(provided.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, Convert.FromHexString(Sign(fields, options.VnPaySecretKey)));
    }

    private static string? Get(Dictionary<string, string> fields, string name) => fields.GetValueOrDefault(name);

    private static Dictionary<string, string>? ReadFields(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                fields[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                    JsonValueKind.Null => string.Empty,
                    _ => throw new JsonException("Nested values are not part of a VNPay message."),
                };
            }

            return fields;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
