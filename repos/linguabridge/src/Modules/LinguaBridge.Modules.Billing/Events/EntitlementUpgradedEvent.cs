using LinguaBridge.Application.Messaging;

namespace LinguaBridge.Modules.Billing.Events;

/// <summary>A learner's paid plan was activated or renewed. Published once per payment, after it is saved.</summary>
public sealed record EntitlementUpgradedEvent(Guid EventId, DateTimeOffset OccurredAt, Guid UserId, string PlanId, DateTimeOffset ExpiresAt)
    : IIntegrationEvent;
