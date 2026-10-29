using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Billing.Abstractions;
using LinguaBridge.Modules.Billing.Commands;
using LinguaBridge.Modules.Billing.Entities;

namespace LinguaBridge.Modules.Billing.Queries;

public sealed record GetEntitlementQuery(Guid UserId);

/// <summary>The plans on sale, cheapest first.</summary>
public sealed class GetPlansHandler(IBillingRepository repository)
{
    public async Task<IReadOnlyList<PlanDto>> HandleAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetPlansAsync(cancellationToken)).Select(p => p.ToDto()).ToList();
}

/// <summary>
/// The learner's current tier. No record means Free; a Vip row whose expiry has passed also means Free, so the answer is
/// right even though nothing rewrites the row when a plan lapses.
/// </summary>
public sealed class GetEntitlementHandler(IBillingRepository repository, IDateTimeProvider clock)
{
    public async Task<EntitlementDto> HandleAsync(GetEntitlementQuery query, CancellationToken cancellationToken = default)
    {
        var entitlement = await repository.GetEntitlementAsync(query.UserId, cancellationToken);

        var active = entitlement is { Tier: EntitlementTier.Vip, PlanId: not null } && (entitlement.ExpiresAt is null || entitlement.ExpiresAt > clock.UtcNow);
        return active
            ? new EntitlementDto(EntitlementTier.Vip.ToString(), entitlement!.PlanId, entitlement.ExpiresAt)
            : new EntitlementDto(EntitlementTier.Free.ToString(), null, null);
    }
}
