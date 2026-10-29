namespace LinguaBridge.Modules.Billing.Abstractions;

public sealed record PlanDto(string Id, string Name, decimal PriceVnd, int DurationDays, int AiDailyTokenQuota);

/// <summary>
/// What the learner is entitled to right now. A paid plan that has run out counts as Free here, even though the stored
/// row still says Vip until the learner next pays.
/// </summary>
/// <param name="Tier"><c>Free</c> or <c>Vip</c>.</param>
public sealed record EntitlementDto(string Tier, string? PlanId, DateTimeOffset? ExpiresAt);

/// <param name="Status"><c>processed</c>, <c>duplicate</c> (already applied earlier), <c>ignored</c> (not a payment that
/// changes anything) or <c>failed</c> (a verified payment that could not be applied; recorded for another attempt).</param>
public sealed record WebhookResultDto(string Status, string? Reason = null);

public static class WebhookStatuses
{
    public const string Processed = "processed";
    public const string Duplicate = "duplicate";
    public const string Ignored = "ignored";
    public const string Failed = "failed";
}
