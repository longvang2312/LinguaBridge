using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Billing.Abstractions;
using LinguaBridge.Modules.Billing.Commands;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Billing.Queries;
using LinguaBridge.Modules.Billing.Webhooks;

namespace LinguaBridge.UnitTests.Billing;

public class WebhookDeduplicationTests
{
    [Fact]
    public async Task A_new_event_makes_the_learner_vip_for_the_plans_duration_and_announces_it()
    {
        var h = new BillingHarness();

        var result = await h.VnPayAsync(h.VnPayPayment("14000001"));

        Assert.Equal(WebhookStatuses.Processed, result.Value.Status);
        var entitlement = h.Repo.Entitlements[h.User];
        Assert.Equal(EntitlementTier.Vip, entitlement.Tier);
        Assert.Equal("vip_monthly", entitlement.PlanId);
        Assert.InRange(entitlement.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30).AddMinutes(1));

        var inbox = Assert.Single(h.Repo.Inbox);
        Assert.Equal(("VNPay", "14000001", WebhookStatus.Processed), (inbox.Provider, inbox.EventId, inbox.Status));
        Assert.NotNull(inbox.ProcessedAt);

        var upgraded = Assert.Single(h.Bus.Upgrades);
        Assert.Equal((h.User, "vip_monthly"), (upgraded.UserId, upgraded.PlanId));
        Assert.Equal(entitlement.ExpiresAt, upgraded.ExpiresAt);
    }

    [Fact]
    public async Task The_same_event_five_times_activates_the_entitlement_exactly_once()
    {
        var h = new BillingHarness();
        var body = h.VnPayPayment("14000001");

        var statuses = new List<string>();
        DateTimeOffset? expiresAfterFirst = null;
        for (var i = 0; i < 5; i++)
        {
            var result = await h.VnPayAsync(body);
            Assert.True(result.IsSuccess, $"delivery {i + 1}");
            statuses.Add(result.Value.Status);
            expiresAfterFirst ??= h.Repo.Entitlements[h.User].ExpiresAt;
        }

        Assert.Equal([WebhookStatuses.Processed, .. Enumerable.Repeat(WebhookStatuses.Duplicate, 4)], statuses);
        // A second activation would have pushed the expiry out by another 30 days.
        Assert.Equal(expiresAfterFirst, h.Repo.Entitlements[h.User].ExpiresAt);
        Assert.Single(h.Repo.Entitlements);
        Assert.Single(h.Repo.Inbox);
        Assert.Single(h.Bus.Upgrades);
        Assert.Equal(1, h.Repo.Commits);
    }

    [Fact]
    public async Task A_redelivery_does_not_change_the_stored_event_or_touch_the_entitlement_row()
    {
        var h = new BillingHarness();
        var body = h.VnPayPayment("14000001");
        await h.VnPayAsync(body);
        var updatedAt = h.Repo.Entitlements[h.User].UpdatedAt;
        var processedAt = h.Repo.Inbox.Single().ProcessedAt;
        var saves = h.Repo.Saves;

        await h.VnPayAsync(body);

        Assert.Equal(updatedAt, h.Repo.Entitlements[h.User].UpdatedAt);
        Assert.Equal(processedAt, h.Repo.Inbox.Single().ProcessedAt);
        Assert.Equal(saves, h.Repo.Saves);
    }

    [Fact]
    public async Task The_same_event_id_from_two_gateways_is_two_different_events()
    {
        var h = new BillingHarness();
        await h.VnPayAsync(h.VnPayPayment("evt_1"));
        var stripeBody = BillingHarness.StripeBody("evt_1", h.User, "vip_monthly", 99_000);

        var result = await h.Handler.HandleAsync(new ProcessWebhookCommand("stripe", stripeBody, h.StripeHeaders(stripeBody)));

        Assert.Equal(WebhookStatuses.Processed, result.Value.Status);
        Assert.Equal(2, h.Repo.Inbox.Count);
    }

    [Fact]
    public async Task A_different_event_for_the_same_learner_renews_from_the_current_expiry()
    {
        var h = new BillingHarness();
        await h.VnPayAsync(h.VnPayPayment("evt_1"));
        var afterFirst = h.Repo.Entitlements[h.User].ExpiresAt!.Value;

        await h.VnPayAsync(h.VnPayPayment("evt_2"));

        Assert.Equal(afterFirst.AddDays(30), h.Repo.Entitlements[h.User].ExpiresAt);
        Assert.Equal(2, h.Bus.Upgrades.Count());
    }

    [Fact]
    public async Task Losing_the_race_on_the_inbox_key_is_a_duplicate_not_an_error()
    {
        var h = new BillingHarness();
        h.Repo.FailNextSaveWithConstraint = ProcessWebhookHandler.InboxUniqueIndex;

        var result = await h.VnPayAsync(h.VnPayPayment("14000001"));

        Assert.Equal(WebhookStatuses.Duplicate, result.Value.Status);
        Assert.Empty(h.Bus.Published);
        Assert.Equal(0, h.Repo.Commits);
    }

    [Fact]
    public async Task Losing_a_race_on_any_other_key_asks_the_gateway_to_retry()
    {
        var h = new BillingHarness();
        h.Repo.FailNextSaveWithConstraint = "pk_user_entitlements";

        var result = await h.VnPayAsync(h.VnPayPayment("14000001"));

        Assert.Equal(ErrorType.Conflict, result.Error!.Type);
        Assert.Empty(h.Bus.Published);
    }

    [Fact]
    public async Task A_failing_subscriber_does_not_fail_or_undo_the_payment()
    {
        var h = new BillingHarness();
        h.Bus.Fail = new InvalidOperationException("subscriber crashed");

        var result = await h.VnPayAsync(h.VnPayPayment("14000001"));

        Assert.Equal(WebhookStatuses.Processed, result.Value.Status);
        Assert.Equal(EntitlementTier.Vip, h.Repo.Entitlements[h.User].Tier);
        Assert.Equal(WebhookStatus.Processed, h.Repo.Inbox.Single().Status);
    }
}

public class WebhookRejectionTests
{
    [Fact]
    public async Task A_bad_signature_is_refused_and_leaves_no_trace()
    {
        var h = new BillingHarness();
        var body = h.VnPayBody(BillingHarness.VnPayFields("14000001", h.User, "vip_monthly", 99_000), secret: "forged");

        var result = await h.VnPayAsync(body);

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Equal("webhook.signature_invalid", result.Error.Code);
        Assert.Empty(h.Repo.Inbox);
        Assert.Empty(h.Repo.Entitlements);
        Assert.Empty(h.Bus.Published);
        Assert.Equal(0, h.Repo.Saves);
    }

    [Fact]
    public async Task A_forged_stripe_replay_with_a_stale_timestamp_is_refused()
    {
        var h = new BillingHarness();
        var body = BillingHarness.StripeBody("evt_1", h.User, "vip_monthly", 99_000);
        var stale = h.StripeHeaders(body, h.Clock.UtcNow.AddHours(-1));

        var result = await h.Handler.HandleAsync(new ProcessWebhookCommand("Stripe", body, stale));

        Assert.Equal("webhook.signature_invalid", result.Error!.Code);
        Assert.Empty(h.Repo.Inbox);
    }

    [Fact]
    public async Task An_unknown_gateway_is_not_found()
    {
        var h = new BillingHarness();

        var result = await h.Handler.HandleAsync(new ProcessWebhookCommand("paypal", "{}", BillingHarness.NoHeaders));

        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
    }

    [Fact]
    public async Task A_payment_that_did_not_succeed_is_acknowledged_and_changes_nothing()
    {
        var h = new BillingHarness();
        var body = h.VnPayBody(BillingHarness.VnPayFields("1", h.User, "vip_monthly", 99_000, responseCode: "24"));

        var result = await h.VnPayAsync(body);

        Assert.Equal(WebhookStatuses.Ignored, result.Value.Status);
        Assert.Empty(h.Repo.Entitlements);
        Assert.Empty(h.Repo.Inbox);
        Assert.Empty(h.Bus.Published);
    }

    [Fact]
    public async Task A_payment_for_an_unknown_plan_is_stored_as_failed_and_applied_when_redelivered_after_the_plan_exists()
    {
        var h = new BillingHarness();
        var body = h.VnPayPayment("14000001", planId: "vip_lifetime", amountVnd: 5_000_000);

        var first = await h.VnPayAsync(body);

        Assert.Equal(WebhookStatuses.Failed, first.Value.Status);
        Assert.Contains("vip_lifetime", first.Value.Reason);
        Assert.Empty(h.Repo.Entitlements);
        Assert.Empty(h.Bus.Published);
        var inbox = Assert.Single(h.Repo.Inbox);
        Assert.Equal(WebhookStatus.Failed, inbox.Status);
        Assert.NotNull(inbox.ErrorMessage);

        h.Repo.Add(new SubscriptionPlan("vip_lifetime", "Lifetime", 5_000_000, 36500, 100_000));
        var retry = await h.VnPayAsync(body);

        Assert.Equal(WebhookStatuses.Processed, retry.Value.Status);
        Assert.Equal(EntitlementTier.Vip, h.Repo.Entitlements[h.User].Tier);
        Assert.Single(h.Repo.Inbox);
        Assert.Equal(WebhookStatus.Processed, inbox.Status);
        Assert.Null(inbox.ErrorMessage);
        Assert.Single(h.Bus.Upgrades);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(98_999)]
    [InlineData(990_000)]
    public async Task Paying_the_wrong_amount_does_not_activate_the_plan(int paid)
    {
        var h = new BillingHarness();

        var result = await h.VnPayAsync(h.VnPayPayment("14000001", amountVnd: paid));

        Assert.Equal(WebhookStatuses.Failed, result.Value.Status);
        Assert.Empty(h.Repo.Entitlements);
        Assert.Equal(WebhookStatus.Failed, h.Repo.Inbox.Single().Status);
    }

    [Fact]
    public async Task A_stripe_payment_in_the_wrong_currency_does_not_activate_the_plan()
    {
        var h = new BillingHarness();
        var body = BillingHarness.StripeBody("evt_1", h.User, "vip_monthly", 99_000, currency: "usd");

        var result = await h.Handler.HandleAsync(new ProcessWebhookCommand("Stripe", body, h.StripeHeaders(body)));

        Assert.Equal(WebhookStatuses.Failed, result.Value.Status);
        Assert.Empty(h.Repo.Entitlements);
    }
}

public class PlanAndEntitlementTests
{
    [Fact]
    public async Task Creating_a_plan_stores_it()
    {
        var h = new BillingHarness();

        var result = await h.CreatePlan.HandleAsync(new CreatePlanCommand("vip_weekly", " VIP weekly ", 29_000, 7, 20_000));

        Assert.Equal(new PlanDto("vip_weekly", "VIP weekly", 29_000, 7, 20_000), result.Value);
        Assert.Contains("vip_weekly", h.Repo.Plans.Keys);
    }

    [Fact]
    public async Task A_duplicate_plan_id_is_a_conflict()
    {
        var h = new BillingHarness();

        var result = await h.CreatePlan.HandleAsync(new CreatePlanCommand("vip_monthly", "Again", 1, 1, 0));

        Assert.Equal(ErrorType.Conflict, result.Error!.Type);
        Assert.Equal("plan.already_exists", result.Error.Code);
        Assert.Equal(99_000, h.Repo.Plans["vip_monthly"].PriceVnd);
    }

    [Theory]
    [InlineData("VIP Monthly", "n", 1, 1, 0, "Id")]
    [InlineData("", "n", 1, 1, 0, "Id")]
    [InlineData("ok", "", 1, 1, 0, "Name")]
    [InlineData("ok", "n", -1, 1, 0, "PriceVnd")]
    [InlineData("ok", "n", 10.5, 1, 0, "PriceVnd")]
    [InlineData("ok", "n", 1, 0, 0, "DurationDays")]
    [InlineData("ok", "n", 1, 1, -5, "AiDailyTokenQuota")]
    public async Task An_invalid_plan_is_a_validation_error_naming_the_field(string id, string name, double price, int days, int quota, string field)
    {
        var h = new BillingHarness();

        var result = await h.CreatePlan.HandleAsync(new CreatePlanCommand(id, name, (decimal)price, days, quota));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Contains(field, result.Error.Details!.Keys);
    }

    [Fact]
    public async Task Plans_are_listed_cheapest_first()
    {
        var h = new BillingHarness();

        var plans = await new GetPlansHandler(h.Repo).HandleAsync();

        Assert.Equal(["vip_monthly", "vip_yearly"], plans.Select(p => p.Id));
    }

    [Fact]
    public async Task A_learner_with_no_record_is_free()
    {
        var h = new BillingHarness();

        var entitlement = await new GetEntitlementHandler(h.Repo, h.Clock).HandleAsync(new GetEntitlementQuery(h.User));

        Assert.Equal(new EntitlementDto("Free", null, null), entitlement);
    }

    [Fact]
    public async Task A_learner_with_an_active_plan_is_vip_until_it_expires()
    {
        var h = new BillingHarness();
        var entitlement = new UserEntitlement(h.User);
        entitlement.ActivatePlan(h.Repo.Plans["vip_monthly"]);
        h.Repo.Add(entitlement);
        h.Clock.UtcNow = DateTimeOffset.UtcNow;
        var handler = new GetEntitlementHandler(h.Repo, h.Clock);

        var active = await handler.HandleAsync(new GetEntitlementQuery(h.User));
        Assert.Equal("Vip", active.Tier);
        Assert.Equal("vip_monthly", active.PlanId);
        Assert.Equal(entitlement.ExpiresAt, active.ExpiresAt);

        h.Clock.UtcNow = entitlement.ExpiresAt!.Value.AddSeconds(1);
        Assert.Equal(new EntitlementDto("Free", null, null), await handler.HandleAsync(new GetEntitlementQuery(h.User)));
    }
}
