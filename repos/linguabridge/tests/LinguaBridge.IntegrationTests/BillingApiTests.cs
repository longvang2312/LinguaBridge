using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LinguaBridge.Infrastructure.Persistence;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Billing.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.IntegrationTests;

/// <summary>
/// Billing end to end on a real PostgreSQL: plans through the admin API, then signed gateway webhooks that change what
/// a learner is entitled to. Each test uses its own learner and its own plan, so the shared database never couples tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BillingApiTests(ContentApiFixture api) : IClassFixture<ContentApiFixture>
{
    private const string Billing = "/api/v1/billing";

    // ---- Access -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plans_can_be_read_by_anyone_but_created_only_by_an_admin()
    {
        var plan = NewPlan();
        using var anonymous = api.Anonymous();
        using var learner = api.LearnerAs(Guid.NewGuid());
        using var admin = api.Admin();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"{Billing}/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{Billing}/plans", plan.Request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await learner.PostAsJsonAsync($"{Billing}/plans", plan.Request)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Billing}/plans", plan.Request)).StatusCode);
    }

    [Fact]
    public async Task The_entitlement_endpoint_needs_a_learner_token()
    {
        using var anonymous = api.Anonymous();
        using var service = api.WithSubject("internal-service");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Billing}/entitlement")).StatusCode);
        var response = await service.GetAsync($"{Billing}/entitlement");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("learner.required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_webhook_endpoint_needs_no_login_only_a_valid_signature()
    {
        var plan = await CreatePlanAsync();
        var body = VnPayBody(Guid.NewGuid(), plan, "tx-anon");
        using var anonymous = api.Anonymous();

        var response = await anonymous.PostAsync($"{Billing}/webhooks/vnpay", Json(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Plans ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_created_plan_appears_in_the_public_list()
    {
        var plan = await CreatePlanAsync(price: 129_000, days: 90, quota: 77_000);
        using var anonymous = api.Anonymous();

        var plans = await (await anonymous.GetAsync($"{Billing}/plans")).Content.ReadFromJsonAsync<JsonElement>();

        var found = plans.EnumerateArray().Single(p => p.GetProperty("id").GetString() == plan.Id);
        Assert.Equal(129_000, found.GetProperty("priceVnd").GetDecimal());
        Assert.Equal(90, found.GetProperty("durationDays").GetInt32());
        Assert.Equal(77_000, found.GetProperty("aiDailyTokenQuota").GetInt32());
    }

    [Fact]
    public async Task A_duplicate_plan_id_is_a_409()
    {
        var plan = await CreatePlanAsync();
        using var admin = api.Admin();

        var response = await admin.PostAsJsonAsync($"{Billing}/plans", plan.Request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("plan.already_exists", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("Bad Id", "n", 1, 1, 0, "Id")]
    [InlineData("ok_id", "", 1, 1, 0, "Name")]
    [InlineData("ok_id", "n", -1, 1, 0, "PriceVnd")]
    [InlineData("ok_id", "n", 1, 0, 0, "DurationDays")]
    [InlineData("ok_id", "n", 1, 1, -1, "AiDailyTokenQuota")]
    [InlineData("ok_id", "n", null, 1, 0, "PriceVnd")]
    public async Task An_invalid_plan_is_a_400_naming_the_field(string id, string name, int? price, int days, int quota, string field)
    {
        using var admin = api.Admin();

        var response = await admin.PostAsJsonAsync($"{Billing}/plans", new { id, name, priceVnd = price, durationDays = days, aiDailyTokenQuota = quota });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty(field, out _));
    }

    // ---- Webhook: signatures ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_bad_signature_is_a_400_and_nothing_is_stored_or_granted()
    {
        var plan = await CreatePlanAsync();
        var user = Guid.NewGuid();
        var forged = VnPayBody(user, plan, "tx-forged", secret: "not-the-secret");
        using var gateway = api.Anonymous();

        var response = await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(forged));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("webhook.signature_invalid", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await using var scope = Scope(out var db);
        Assert.False(await db.WebhookInbox.AnyAsync(w => w.EventId == "tx-forged"));
        Assert.False(await db.UserEntitlements.AnyAsync(e => e.UserId == user));
    }

    [Fact]
    public async Task A_tampered_amount_or_learner_breaks_the_signature()
    {
        var plan = await CreatePlanAsync();
        var user = Guid.NewGuid();
        var fields = VnPayFields(user, plan, "tx-tamper");
        var signature = VnPayWebhookProvider.Sign(fields, ContentApiFixture.VnPaySecret);
        using var gateway = api.Anonymous();

        fields["vnp_Amount"] = "100";
        fields["vnp_SecureHash"] = signature;

        var response = await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(JsonSerializer.Serialize(fields)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_stripe_message_needs_a_valid_signature_inside_the_time_window()
    {
        var plan = await CreatePlanAsync();
        var user = Guid.NewGuid();
        var body = StripeBody($"evt_{Guid.NewGuid():N}", user, plan);
        using var gateway = api.Anonymous();

        Assert.Equal(HttpStatusCode.BadRequest, (await gateway.PostAsync($"{Billing}/webhooks/stripe", Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendStripeAsync(gateway, body, at: DateTimeOffset.UtcNow.AddHours(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendStripeAsync(gateway, body, secret: "whsec_wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendStripeAsync(gateway, body)).StatusCode);
        Assert.Equal("Vip", await TierAsync(user));
    }

    [Fact]
    public async Task An_unknown_gateway_is_a_404()
    {
        using var gateway = api.Anonymous();

        Assert.Equal(HttpStatusCode.NotFound, (await gateway.PostAsync($"{Billing}/webhooks/paypal", Json("{}"))).StatusCode);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_read()
    {
        using var gateway = api.Anonymous();

        var response = await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json("{\"vnp_X\":\"" + new string('a', 300_000) + "\"}"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // ---- Webhook: replay protection ---------------------------------------------------------------------------

    [Fact]
    public async Task The_same_webhook_five_times_activates_the_entitlement_exactly_once()
    {
        var plan = await CreatePlanAsync(days: 30);
        var user = Guid.NewGuid();
        var body = VnPayBody(user, plan, "tx-replay");
        using var gateway = api.Anonymous();

        var statuses = new List<string?>();
        DateTimeOffset? expiresAfterFirst = null;
        for (var i = 0; i < 5; i++)
        {
            var response = await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(body));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            statuses.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
            expiresAfterFirst ??= (await ReadEntitlementAsync(user))!.ExpiresAt;
        }

        Assert.Equal(["processed", "duplicate", "duplicate", "duplicate", "duplicate"], statuses);
        var entitlement = (await ReadEntitlementAsync(user))!;
        Assert.Equal(expiresAfterFirst, entitlement.ExpiresAt);
        Assert.InRange(entitlement.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30).AddMinutes(5));

        await using var scope = Scope(out var db);
        var row = await db.WebhookInbox.SingleAsync(w => w.Provider == "VNPay" && w.EventId == "tx-replay");
        Assert.Equal(WebhookStatus.Processed, row.Status);
        Assert.Single(api.Upgrades.ForUser(user));
    }

    [Fact]
    public async Task Twenty_simultaneous_deliveries_of_one_event_apply_it_once()
    {
        var plan = await CreatePlanAsync(days: 30);
        var user = Guid.NewGuid();
        var body = VnPayBody(user, plan, "tx-parallel");
        using var gateway = api.Anonymous();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(body))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var statuses = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()));
        Assert.Equal(1, statuses.Count(s => s == "processed"));
        Assert.Equal(19, statuses.Count(s => s == "duplicate"));

        Assert.InRange((await ReadEntitlementAsync(user))!.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30).AddMinutes(5));
        await using var scope = Scope(out var db);
        Assert.Equal(1, await db.WebhookInbox.CountAsync(w => w.EventId == "tx-parallel"));
        Assert.Single(api.Upgrades.ForUser(user));
    }

    [Fact]
    public async Task Simultaneous_different_payments_from_one_learner_all_count_and_none_is_lost()
    {
        var plan = await CreatePlanAsync(days: 30);
        var user = Guid.NewGuid();
        using var gateway = api.Anonymous();

        var responses = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(i => gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(VnPayBody(user, plan, $"tx-renew-{i}")))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        // Three paid periods back to back: a lost update would leave 60 or 30 days.
        Assert.InRange((await ReadEntitlementAsync(user))!.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddDays(90).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90).AddMinutes(5));
        Assert.Equal(3, api.Upgrades.ForUser(user).Count);
    }

    // ---- Webhook: outcomes ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_paid_stripe_checkout_makes_the_learner_vip_and_the_ai_quota_follows_the_plan()
    {
        var plan = await CreatePlanAsync(price: 199_000, days: 30, quota: 123_456);
        var user = Guid.NewGuid();
        var body = StripeBody($"evt_{Guid.NewGuid():N}", user, plan);
        using var gateway = api.Anonymous();
        using var learner = api.LearnerAs(user);

        Assert.Equal("Free", await TierAsync(user));
        Assert.Equal(HttpStatusCode.OK, (await SendStripeAsync(gateway, body)).StatusCode);

        var entitlement = await (await learner.GetAsync($"{Billing}/entitlement")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Vip", entitlement.GetProperty("tier").GetString());
        Assert.Equal(plan.Id, entitlement.GetProperty("planId").GetString());
        Assert.True(entitlement.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29));

        // The AI module reads the same entitlement: the learner now has the plan's token quota instead of the free allowance.
        var quota = await (await learner.GetAsync("/api/v1/ai/quota")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paid", quota.GetProperty("plan").GetString());
        Assert.Equal(123_456, quota.GetProperty("dailyTokenLimit").GetInt64());

        var published = Assert.Single(api.Upgrades.ForUser(user));
        Assert.Equal(plan.Id, published.PlanId);
        Assert.Equal(entitlement.GetProperty("expiresAt").GetDateTimeOffset(), published.ExpiresAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task A_learner_who_never_paid_is_free()
    {
        using var learner = api.LearnerAs(Guid.NewGuid());

        var entitlement = await (await learner.GetAsync($"{Billing}/entitlement")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Free", entitlement.GetProperty("tier").GetString());
        Assert.Equal(JsonValueKind.Null, entitlement.GetProperty("planId").ValueKind);
        Assert.Equal(JsonValueKind.Null, entitlement.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    public async Task An_expired_plan_reads_as_free()
    {
        var plan = await CreatePlanAsync();
        var user = Guid.NewGuid();
        using var gateway = api.Anonymous();
        await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(VnPayBody(user, plan, "tx-expire")));
        await using (var scope = Scope(out var db))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE billing.user_entitlements SET expires_at = {DateTimeOffset.UtcNow.AddDays(-1)} WHERE user_id = {user}");

        Assert.Equal("Free", await TierAsync(user));
    }

    [Fact]
    public async Task A_payment_that_did_not_succeed_is_acknowledged_and_grants_nothing()
    {
        var plan = await CreatePlanAsync();
        var user = Guid.NewGuid();
        var body = Signed(VnPayFields(user, plan, "tx-declined", responseCode: "24"));
        using var gateway = api.Anonymous();

        var response = await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ignored", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("Free", await TierAsync(user));
    }

    [Fact]
    public async Task A_payment_for_the_wrong_amount_is_a_422_stored_as_failed_and_grants_nothing()
    {
        var plan = await CreatePlanAsync(price: 99_000);
        var user = Guid.NewGuid();
        var body = Signed(VnPayFields(user, plan, "tx-cheap", amountVnd: 1_000));
        using var gateway = api.Anonymous();

        var response = await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(body));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("Free", await TierAsync(user));
        await using var scope = Scope(out var db);
        var row = await db.WebhookInbox.SingleAsync(w => w.EventId == "tx-cheap");
        Assert.Equal(WebhookStatus.Failed, row.Status);
        Assert.Contains("does not match", row.ErrorMessage);
        Assert.Empty(api.Upgrades.ForUser(user));
    }

    [Fact]
    public async Task A_failed_payment_is_applied_when_the_gateway_redelivers_it_after_the_plan_exists()
    {
        var planId = $"late_{Guid.NewGuid():N}"[..30];
        var user = Guid.NewGuid();
        var body = Signed(VnPayFields(user, new PlanSpec(planId, 99_000, 30, 1000), "tx-late"));
        using var gateway = api.Anonymous();

        Assert.Equal((HttpStatusCode)422, (await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(body))).StatusCode);
        Assert.Equal("Free", await TierAsync(user));

        using var admin = api.Admin();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Billing}/plans", new { id = planId, name = "Late", priceVnd = 99_000, durationDays = 30, aiDailyTokenQuota = 1000 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gateway.PostAsync($"{Billing}/webhooks/vnpay", Json(body))).StatusCode);

        Assert.Equal("Vip", await TierAsync(user));
        await using var scope = Scope(out var db);
        Assert.Equal(1, await db.WebhookInbox.CountAsync(w => w.EventId == "tx-late"));
        Assert.Equal(WebhookStatus.Processed, (await db.WebhookInbox.SingleAsync(w => w.EventId == "tx-late")).Status);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private sealed record PlanSpec(string Id, int Price, int Days, int Quota)
    {
        public object Request => new { id = Id, name = $"Plan {Id}", priceVnd = Price, durationDays = Days, aiDailyTokenQuota = Quota };
    }

    private static PlanSpec NewPlan(int price = 99_000, int days = 30, int quota = 50_000) =>
        new($"plan_{Guid.NewGuid():N}"[..32], price, days, quota);

    private async Task<PlanSpec> CreatePlanAsync(int price = 99_000, int days = 30, int quota = 50_000)
    {
        var plan = NewPlan(price, days, quota);
        using var admin = api.Admin();
        var response = await admin.PostAsJsonAsync($"{Billing}/plans", plan.Request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return plan;
    }

    private static Dictionary<string, string> VnPayFields(Guid user, PlanSpec plan, string transactionNo, int? amountVnd = null, string responseCode = "00") => new()
    {
        ["vnp_Amount"] = ((amountVnd ?? plan.Price) * 100L).ToString(),
        ["vnp_BankCode"] = "NCB",
        ["vnp_OrderInfo"] = OrderReference.Format(user, plan.Id),
        ["vnp_ResponseCode"] = responseCode,
        ["vnp_TransactionNo"] = transactionNo,
        ["vnp_TransactionStatus"] = responseCode,
        ["vnp_TxnRef"] = "ORDER-1",
    };

    private static string Signed(Dictionary<string, string> fields, string? secret = null)
    {
        var copy = new Dictionary<string, string>(fields) { ["vnp_SecureHash"] = VnPayWebhookProvider.Sign(fields, secret ?? ContentApiFixture.VnPaySecret) };
        return JsonSerializer.Serialize(copy);
    }

    private static string VnPayBody(Guid user, PlanSpec plan, string transactionNo, string? secret = null) =>
        Signed(VnPayFields(user, plan, transactionNo), secret);

    private static string StripeBody(string eventId, Guid user, PlanSpec plan) => JsonSerializer.Serialize(new
    {
        id = eventId,
        type = "checkout.session.completed",
        data = new { @object = new { client_reference_id = OrderReference.Format(user, plan.Id), payment_status = "paid", amount_total = plan.Price, currency = "vnd" } },
    });

    private static Task<HttpResponseMessage> SendStripeAsync(HttpClient gateway, string body, DateTimeOffset? at = null, string? secret = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Billing}/webhooks/stripe") { Content = Json(body) };
        request.Headers.Add(StripeWebhookProvider.SignatureHeader, StripeWebhookProvider.Sign(body, secret ?? ContentApiFixture.StripeSecret, at ?? DateTimeOffset.UtcNow));
        return gateway.SendAsync(request);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

    private async Task<string> TierAsync(Guid user)
    {
        using var learner = api.LearnerAs(user);
        return (await (await learner.GetAsync($"{Billing}/entitlement")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tier").GetString()!;
    }

    private async Task<UserEntitlement?> ReadEntitlementAsync(Guid user)
    {
        await using var scope = Scope(out var db);
        return await db.UserEntitlements.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == user);
    }

    private AsyncServiceScope Scope(out LinguaBridgeDbContext db)
    {
        var scope = api.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<LinguaBridgeDbContext>();
        return scope;
    }
}
