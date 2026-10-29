using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.Billing.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinguaBridge.Infrastructure.Persistence.AiTutor;

/// <summary>
/// Answers AiTutor's question about the learner's plan by reading the billing schema. A learner with no entitlement
/// row, on the free tier, or whose plan has expired has no paid quota, so the free allowance applies.
/// </summary>
internal sealed class UserEntitlementSource(LinguaBridgeDbContext db) : IUserEntitlementSource
{
    public async Task<int?> GetPaidDailyTokenQuotaAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await (
                from e in db.UserEntitlements.AsNoTracking()
                join p in db.SubscriptionPlans.AsNoTracking() on e.PlanId equals p.Id
                where e.UserId == userId && e.Tier == EntitlementTier.Vip && (e.ExpiresAt == null || e.ExpiresAt > now)
                select (int?)p.AiDailyTokenQuota)
            .SingleOrDefaultAsync(cancellationToken);
}
