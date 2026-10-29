using LinguaBridge.Modules.Billing.Entities;

namespace LinguaBridge.Modules.Billing.Abstractions;

/// <summary>A unit of work for one learner: while it is open, no other billing change for that learner can interleave.</summary>
public interface IBillingTransaction : IAsyncDisposable
{
    /// <summary>Makes the saved changes permanent. Disposing without committing discards them.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IBillingRepository
{
    Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(CancellationToken cancellationToken);

    Task<SubscriptionPlan?> GetPlanAsync(string planId, CancellationToken cancellationToken);

    void Add(SubscriptionPlan plan);

    Task<UserEntitlement?> GetEntitlementAsync(Guid userId, CancellationToken cancellationToken);

    void Add(UserEntitlement entitlement);

    Task<WebhookInbox?> GetInboxAsync(string provider, string eventId, CancellationToken cancellationToken);

    void Add(WebhookInbox inbox);

    /// <summary>
    /// Opens a transaction that holds a lock on this learner until it ends, so two webhooks for the same learner
    /// (a redelivery, or two renewals) are handled one after the other, each seeing the other's result.
    /// </summary>
    Task<IBillingTransaction> BeginAsync(Guid userId, CancellationToken cancellationToken);

    /// <exception cref="LinguaBridge.Application.Abstractions.UniqueConstraintViolationException">A unique key was already taken.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
