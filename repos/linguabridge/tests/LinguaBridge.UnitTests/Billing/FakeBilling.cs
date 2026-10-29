using System.Text.Json;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Application.Messaging;
using LinguaBridge.Modules.Billing.Abstractions;
using LinguaBridge.Modules.Billing.Commands;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Billing.Events;
using LinguaBridge.Modules.Billing.Webhooks;
using LinguaBridge.UnitTests.Learning;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinguaBridge.UnitTests.Billing;

internal sealed class FakeBillingRepository : IBillingRepository
{
    public Dictionary<string, SubscriptionPlan> Plans { get; } = [];
    public Dictionary<Guid, UserEntitlement> Entitlements { get; } = [];
    public List<WebhookInbox> Inbox { get; } = [];
    public int Saves { get; private set; }
    public int Commits { get; private set; }

    /// <summary>Makes the next save fail as if another request had just stored the same key.</summary>
    public string? FailNextSaveWithConstraint { get; set; }

    public Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SubscriptionPlan>>(Plans.Values.OrderBy(p => p.PriceVnd).ThenBy(p => p.Id).ToList());

    public Task<SubscriptionPlan?> GetPlanAsync(string planId, CancellationToken cancellationToken) =>
        Task.FromResult(Plans.GetValueOrDefault(planId));

    public void Add(SubscriptionPlan plan) => Plans[plan.Id] = plan;

    public Task<UserEntitlement?> GetEntitlementAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Entitlements.GetValueOrDefault(userId));

    public void Add(UserEntitlement entitlement) => Entitlements[entitlement.UserId] = entitlement;

    public Task<WebhookInbox?> GetInboxAsync(string provider, string eventId, CancellationToken cancellationToken) =>
        Task.FromResult(Inbox.SingleOrDefault(w => w.Provider == provider && w.EventId == eventId));

    public void Add(WebhookInbox inbox)
    {
        if (!Inbox.Contains(inbox))
            Inbox.Add(inbox);
    }

    public Task<IBillingTransaction> BeginAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IBillingTransaction>(new FakeTransaction(this));

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailNextSaveWithConstraint is { } constraint)
        {
            FailNextSaveWithConstraint = null;
            throw new UniqueConstraintViolationException(constraint, new Exception("duplicate key"));
        }

        Saves++;
        return Task.CompletedTask;
    }

    private sealed class FakeTransaction(FakeBillingRepository repository) : IBillingTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            repository.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class RecordingEventBus : IEventBus
{
    public List<IIntegrationEvent> Published { get; } = [];
    public Exception? Fail { get; set; }

    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        Published.Add(integrationEvent);
        return Fail is null ? Task.CompletedTask : Task.FromException(Fail);
    }

    public IEnumerable<EntitlementUpgradedEvent> Upgrades => Published.OfType<EntitlementUpgradedEvent>();
}

/// <summary>Builds correctly signed messages the way each gateway does, and a handler wired like the host's.</summary>
internal sealed class BillingHarness
{
    public const string VnPaySecret = "vnpay-test-secret";
    public const string StripeSecret = "whsec_test";

    public readonly FakeClock Clock = new();
    public readonly FakeBillingRepository Repo = new();
    public readonly RecordingEventBus Bus = new();
    public readonly WebhookOptions Options = new() { VnPaySecretKey = VnPaySecret, StripeSecretKey = StripeSecret };
    public readonly Guid User = Guid.NewGuid();

    public BillingHarness()
    {
        Repo.Add(new SubscriptionPlan("vip_monthly", "VIP monthly", 99_000, 30, 50_000));
        Repo.Add(new SubscriptionPlan("vip_yearly", "VIP yearly", 990_000, 365, 80_000));
    }

    public IWebhookProvider[] Providers => [new VnPayWebhookProvider(Options), new StripeWebhookProvider(Options, Clock)];

    public ProcessWebhookHandler Handler => new(Providers, Repo, Bus, Clock, NullLogger<ProcessWebhookHandler>.Instance);

    public CreatePlanHandler CreatePlan => new(Repo, new CreatePlanValidator());

    public static Dictionary<string, string> VnPayFields(string transactionNo, Guid user, string planId, decimal amountVnd, string responseCode = "00", string status = "00") => new()
    {
        ["vnp_Amount"] = (amountVnd * 100).ToString("0"),
        ["vnp_BankCode"] = "NCB",
        ["vnp_OrderInfo"] = OrderReference.Format(user, planId),
        ["vnp_ResponseCode"] = responseCode,
        ["vnp_TransactionNo"] = transactionNo,
        ["vnp_TransactionStatus"] = status,
        ["vnp_TxnRef"] = "ORDER-1",
    };

    public string VnPayBody(Dictionary<string, string> fields, string? secret = null)
    {
        var signed = new Dictionary<string, string>(fields) { ["vnp_SecureHash"] = VnPayWebhookProvider.Sign(fields, secret ?? VnPaySecret) };
        return JsonSerializer.Serialize(signed);
    }

    public string VnPayPayment(string transactionNo, string planId = "vip_monthly", decimal amountVnd = 99_000, Guid? user = null) =>
        VnPayBody(VnPayFields(transactionNo, user ?? User, planId, amountVnd));

    public static string StripeBody(string eventId, Guid user, string planId, long amount, string type = "checkout.session.completed", string paymentStatus = "paid", string currency = "vnd") =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            type,
            data = new { @object = new { client_reference_id = OrderReference.Format(user, planId), payment_status = paymentStatus, amount_total = amount, currency } },
        });

    public IReadOnlyDictionary<string, string> StripeHeaders(string body, DateTimeOffset? at = null, string? secret = null) =>
        new Dictionary<string, string> { [StripeWebhookProvider.SignatureHeader] = StripeWebhookProvider.Sign(body, secret ?? StripeSecret, at ?? Clock.UtcNow) };

    public static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public Task<Result<WebhookResultDto>> VnPayAsync(string body) => Handler.HandleAsync(new ProcessWebhookCommand("vnpay", body, NoHeaders));
}
