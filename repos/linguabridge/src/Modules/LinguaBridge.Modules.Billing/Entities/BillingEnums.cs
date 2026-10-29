namespace LinguaBridge.Modules.Billing.Entities;

/// <summary>Access tier a learner is entitled to. Stored by name.</summary>
public enum EntitlementTier
{
    Free,
    Vip,
}

/// <summary>Processing state of an inbound payment webhook. Stored by name.</summary>
public enum WebhookStatus
{
    Pending,
    Processed,
    Failed,
}
