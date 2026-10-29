using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Billing.Abstractions;
using LinguaBridge.Modules.Billing.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace LinguaBridge.Infrastructure.Persistence.Billing;

/// <summary>Reads and writes only the <c>billing</c> schema.</summary>
internal sealed class BillingRepository(LinguaBridgeDbContext db) : IBillingRepository
{
    public async Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(CancellationToken cancellationToken) =>
        await db.SubscriptionPlans.AsNoTracking().OrderBy(p => p.PriceVnd).ThenBy(p => p.Id).ToListAsync(cancellationToken);

    public Task<SubscriptionPlan?> GetPlanAsync(string planId, CancellationToken cancellationToken) =>
        db.SubscriptionPlans.SingleOrDefaultAsync(p => p.Id == planId, cancellationToken);

    public void Add(SubscriptionPlan plan) => db.SubscriptionPlans.Add(plan);

    public Task<UserEntitlement?> GetEntitlementAsync(Guid userId, CancellationToken cancellationToken) =>
        db.UserEntitlements.SingleOrDefaultAsync(e => e.UserId == userId, cancellationToken);

    public void Add(UserEntitlement entitlement) => db.UserEntitlements.Add(entitlement);

    public Task<WebhookInbox?> GetInboxAsync(string provider, string eventId, CancellationToken cancellationToken) =>
        db.WebhookInbox.SingleOrDefaultAsync(w => w.Provider == provider && w.EventId == eventId, cancellationToken);

    public void Add(WebhookInbox inbox) => db.WebhookInbox.Add(inbox);

    public async Task<IBillingTransaction> BeginAsync(Guid userId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Held until the transaction ends. Webhooks for one learner queue here; other learners are not delayed.
            var key = $"billing:{userId}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }

        return new Transaction(transaction);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            throw new UniqueConstraintViolationException(postgres.ConstraintName, ex);
        }
    }

    private sealed class Transaction(IDbContextTransaction inner) : IBillingTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => inner.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
