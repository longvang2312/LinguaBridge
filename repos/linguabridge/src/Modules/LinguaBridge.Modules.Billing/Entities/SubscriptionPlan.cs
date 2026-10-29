using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.Billing.Entities;

/// <summary>A purchasable plan, identified by a readable code such as <c>vip_monthly</c>.</summary>
public class SubscriptionPlan
{
    public const int IdMaxLength = 50;
    public const int NameMaxLength = 100;

    // Required by EF Core.
    private SubscriptionPlan() { }

    public SubscriptionPlan(string id, string name, decimal priceVnd, int durationDays, int aiDailyTokenQuota)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priceVnd);
        if (decimal.Truncate(priceVnd) != priceVnd)
            throw new ArgumentException("VND prices have no fractional part.", nameof(priceVnd));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationDays);

        Id = Guard.Required(id, IdMaxLength, nameof(id));
        Name = Guard.Required(name, NameMaxLength, nameof(name));
        PriceVnd = priceVnd;
        DurationDays = durationDays;
        AiDailyTokenQuota = Guard.NonNegative(aiDailyTokenQuota, nameof(aiDailyTokenQuota));
    }

    public string Id { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public decimal PriceVnd { get; private set; }
    public int DurationDays { get; private set; }
    public int AiDailyTokenQuota { get; private set; }
}
