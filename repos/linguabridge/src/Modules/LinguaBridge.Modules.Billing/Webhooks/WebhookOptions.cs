using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace LinguaBridge.Modules.Billing.Webhooks;

/// <summary>
/// Secrets from <c>Billing:Webhooks</c>. A provider with no secret accepts nothing: an unsigned webhook could
/// otherwise hand out paid plans to anyone who can guess the URL.
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Billing:Webhooks";

    /// <summary>VNPay hash secret (<c>vnp_HashSecret</c>).</summary>
    public string VnPaySecretKey { get; init; } = string.Empty;

    /// <summary>Stripe endpoint signing secret (<c>whsec_...</c>).</summary>
    public string StripeSecretKey { get; init; } = string.Empty;

    /// <summary>How far a Stripe signature timestamp may be from now; older or future-dated deliveries are rejected as replays.</summary>
    public int StripeToleranceSeconds { get; init; } = 300;

    public static WebhookOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new WebhookOptions
        {
            VnPaySecretKey = Text(configuration, "VNPay:SecretKey"),
            StripeSecretKey = Text(configuration, "Stripe:SecretKey"),
            StripeToleranceSeconds = Tolerance(configuration),
        };
    }

    private static string Text(IConfiguration configuration, string name) => configuration[$"{SectionName}:{name}"]?.Trim() ?? string.Empty;

    private static int Tolerance(IConfiguration configuration)
    {
        var key = $"{SectionName}:Stripe:ToleranceSeconds";
        var text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
            return 300;

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"Configuration '{key}' must be a positive integer, but is '{text}'.");
    }
}
