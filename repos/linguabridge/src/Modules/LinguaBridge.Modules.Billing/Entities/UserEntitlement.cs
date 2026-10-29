using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.Billing.Entities;

/// <summary>What a learner is currently entitled to. The single source of truth for paid-feature authorization.</summary>
public class UserEntitlement
{
    // Required by EF Core.
    private UserEntitlement() { }

    /// <summary>Every learner starts on the free tier.</summary>
    public UserEntitlement(Guid userId)
    {
        UserId = Guard.RequiredId(userId, nameof(userId));
        Tier = EntitlementTier.Free;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }
    public EntitlementTier Tier { get; private set; }
    public string? PlanId { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Moves the learner onto <paramref name="plan"/>. Renewing before expiry extends from the current expiry,
    /// so paid days are never lost.
    /// </summary>
    public void ActivatePlan(SubscriptionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var now = DateTimeOffset.UtcNow;
        var start = ExpiresAt is { } expiresAt && expiresAt > now ? expiresAt : now;

        Tier = EntitlementTier.Vip;
        PlanId = plan.Id;
        ExpiresAt = start.AddDays(plan.DurationDays);
        UpdatedAt = now;
    }

    public void RevertToFree()
    {
        Tier = EntitlementTier.Free;
        PlanId = null;
        ExpiresAt = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
