using System.Text.Json;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Billing.Webhooks;

namespace LinguaBridge.UnitTests.Billing;

public class VnPayProviderTests
{
    private readonly BillingHarness _h = new();

    private Result<WebhookNotification> Verify(string body) => new VnPayWebhookProvider(_h.Options).Verify(new WebhookRequest(body, BillingHarness.NoHeaders));

    [Fact]
    public void A_correctly_signed_successful_payment_is_read_as_a_payment()
    {
        var result = Verify(_h.VnPayPayment("14000001"));

        var payment = Assert.IsType<PaymentConfirmed>(result.Value);
        Assert.Equal("14000001", payment.EventId);
        Assert.Equal(_h.User, payment.UserId);
        Assert.Equal("vip_monthly", payment.PlanId);
        Assert.Equal(99_000m, payment.AmountVnd);
    }

    [Fact]
    public void The_signature_follows_the_documented_algorithm()
    {
        // HMAC-SHA512 over the sorted, url-encoded, non-empty vnp_* fields, computed here independently of the provider.
        var fields = new Dictionary<string, string> { ["vnp_B"] = "2 x", ["vnp_A"] = "1", ["vnp_Empty"] = "", ["other"] = "ignored", ["vnp_SecureHashType"] = "HmacSHA512" };
        var data = "vnp_A=1&vnp_B=2+x";
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA512.HashData(
            System.Text.Encoding.UTF8.GetBytes("k"), System.Text.Encoding.UTF8.GetBytes(data)));

        Assert.Equal(expected, VnPayWebhookProvider.Sign(fields, "k"));
    }

    [Fact]
    public void A_signature_in_lower_case_hex_is_accepted()
    {
        var fields = BillingHarness.VnPayFields("1", _h.User, "vip_monthly", 99_000);
        fields["vnp_SecureHash"] = VnPayWebhookProvider.Sign(fields, BillingHarness.VnPaySecret).ToLowerInvariant();

        Assert.True(Verify(JsonSerializer.Serialize(fields)).IsSuccess);
    }

    [Fact]
    public void Changing_any_signed_field_breaks_the_signature()
    {
        var fields = BillingHarness.VnPayFields("14000001", _h.User, "vip_monthly", 99_000);
        fields["vnp_SecureHash"] = VnPayWebhookProvider.Sign(fields, BillingHarness.VnPaySecret);

        foreach (var (field, tampered) in new[] { ("vnp_Amount", "100"), ("vnp_OrderInfo", OrderReference.Format(Guid.NewGuid(), "vip_monthly")), ("vnp_TransactionNo", "99999999") })
        {
            var copy = new Dictionary<string, string>(fields) { [field] = tampered };

            var error = Verify(JsonSerializer.Serialize(copy)).Error;

            Assert.Equal("webhook.signature_invalid", error!.Code);
        }
    }

    [Fact]
    public void A_message_signed_with_another_secret_is_refused()
    {
        var body = _h.VnPayBody(BillingHarness.VnPayFields("1", _h.User, "vip_monthly", 99_000), secret: "attacker-secret");

        Assert.Equal("webhook.signature_invalid", Verify(body).Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("ABCD")]
    public void A_missing_or_malformed_hash_is_refused(string? hash)
    {
        var fields = BillingHarness.VnPayFields("1", _h.User, "vip_monthly", 99_000);
        if (hash is not null)
            fields["vnp_SecureHash"] = hash;

        Assert.Equal("webhook.signature_invalid", Verify(JsonSerializer.Serialize(fields)).Error!.Code);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"vnp_Nested":{"a":1}}""")]
    public void A_body_that_is_not_a_flat_object_is_a_payload_error(string body)
    {
        Assert.Equal("webhook.payload_invalid", Verify(body).Error!.Code);
    }

    [Theory]
    [InlineData("24", "00")]
    [InlineData("00", "02")]
    public void A_payment_that_did_not_succeed_is_ignored_not_applied(string responseCode, string status)
    {
        var body = _h.VnPayBody(BillingHarness.VnPayFields("1", _h.User, "vip_monthly", 99_000, responseCode, status));

        Assert.IsType<IgnoredNotification>(Verify(body).Value);
    }

    [Fact]
    public void A_signed_message_without_a_readable_order_reference_is_a_payload_error()
    {
        var fields = BillingHarness.VnPayFields("1", _h.User, "vip_monthly", 99_000);
        fields["vnp_OrderInfo"] = "thanh toan don hang";

        Assert.Equal("webhook.payload_invalid", Verify(_h.VnPayBody(fields)).Error!.Code);
    }

    [Fact]
    public void Without_a_configured_secret_nothing_is_accepted()
    {
        var provider = new VnPayWebhookProvider(new WebhookOptions());

        var error = provider.Verify(new WebhookRequest(_h.VnPayPayment("1"), BillingHarness.NoHeaders)).Error;

        Assert.Equal("webhook.not_configured", error!.Code);
    }
}

public class StripeProviderTests
{
    private readonly BillingHarness _h = new();

    private Result<WebhookNotification> Verify(string body, IReadOnlyDictionary<string, string>? headers = null) =>
        new StripeWebhookProvider(_h.Options, _h.Clock).Verify(new WebhookRequest(body, headers ?? _h.StripeHeaders(body)));

    [Fact]
    public void A_correctly_signed_paid_checkout_is_read_as_a_payment()
    {
        var body = BillingHarness.StripeBody("evt_1", _h.User, "vip_monthly", 99_000);

        var payment = Assert.IsType<PaymentConfirmed>(Verify(body).Value);

        Assert.Equal("evt_1", payment.EventId);
        Assert.Equal(_h.User, payment.UserId);
        Assert.Equal("vip_monthly", payment.PlanId);
        Assert.Equal(99_000m, payment.AmountVnd);
    }

    [Fact]
    public void A_changed_body_breaks_the_signature()
    {
        var body = BillingHarness.StripeBody("evt_1", _h.User, "vip_monthly", 99_000);
        var headers = _h.StripeHeaders(body);

        var tampered = BillingHarness.StripeBody("evt_1", _h.User, "vip_yearly", 99_000);

        Assert.Equal("webhook.signature_invalid", Verify(tampered, headers).Error!.Code);
    }

    [Fact]
    public void A_message_signed_with_another_secret_is_refused()
    {
        var body = BillingHarness.StripeBody("evt_1", _h.User, "vip_monthly", 99_000);

        Assert.Equal("webhook.signature_invalid", Verify(body, _h.StripeHeaders(body, secret: "whsec_attacker")).Error!.Code);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void A_timestamp_outside_the_tolerance_is_a_replay_and_is_refused(int secondsFromNow)
    {
        var body = BillingHarness.StripeBody("evt_1", _h.User, "vip_monthly", 99_000);

        var headers = _h.StripeHeaders(body, _h.Clock.UtcNow.AddSeconds(secondsFromNow));

        Assert.Equal("webhook.signature_invalid", Verify(body, headers).Error!.Code);
    }

    [Fact]
    public void A_timestamp_inside_the_tolerance_is_accepted()
    {
        var body = BillingHarness.StripeBody("evt_1", _h.User, "vip_monthly", 99_000);

        Assert.True(Verify(body, _h.StripeHeaders(body, _h.Clock.UtcNow.AddSeconds(-290))).IsSuccess);
    }

    [Fact]
    public void Any_one_of_several_v1_signatures_may_match()
    {
        var body = BillingHarness.StripeBody("evt_1", _h.User, "vip_monthly", 99_000);
        var good = StripeWebhookProvider.Sign(body, BillingHarness.StripeSecret, _h.Clock.UtcNow);
        var header = good + ",v1=" + new string('0', 64);

        Assert.True(Verify(body, new Dictionary<string, string> { [StripeWebhookProvider.SignatureHeader] = header }).IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("v1=00")]
    [InlineData("t=1")]
    public void A_missing_or_malformed_header_is_refused(string header)
    {
        var body = BillingHarness.StripeBody("evt_1", _h.User, "vip_monthly", 99_000);

        Assert.Equal("webhook.signature_invalid", Verify(body, new Dictionary<string, string> { [StripeWebhookProvider.SignatureHeader] = header }).Error!.Code);
        Assert.Equal("webhook.signature_invalid", Verify(body, BillingHarness.NoHeaders).Error!.Code);
    }

    [Fact]
    public void An_unrelated_event_type_is_ignored()
    {
        var body = BillingHarness.StripeBody("evt_2", _h.User, "vip_monthly", 99_000, type: "invoice.created");

        Assert.IsType<IgnoredNotification>(Verify(body).Value);
    }

    [Fact]
    public void A_checkout_that_is_not_paid_yet_is_ignored()
    {
        var body = BillingHarness.StripeBody("evt_3", _h.User, "vip_monthly", 99_000, paymentStatus: "unpaid");

        Assert.IsType<IgnoredNotification>(Verify(body).Value);
    }

    [Fact]
    public void A_delayed_payment_that_later_succeeds_counts()
    {
        var body = BillingHarness.StripeBody("evt_4", _h.User, "vip_monthly", 99_000, type: "checkout.session.async_payment_succeeded");

        Assert.IsType<PaymentConfirmed>(Verify(body).Value);
    }

    [Fact]
    public void A_payment_in_another_currency_has_no_vnd_amount()
    {
        var body = BillingHarness.StripeBody("evt_5", _h.User, "vip_monthly", 99_000, currency: "usd");

        Assert.Null(Assert.IsType<PaymentConfirmed>(Verify(body).Value).AmountVnd);
    }

    [Fact]
    public void A_signed_body_that_is_not_an_event_is_a_payload_error()
    {
        const string Body = """{"hello":"world"}""";

        Assert.Equal("webhook.payload_invalid", Verify(Body).Error!.Code);
    }

    [Fact]
    public void Without_a_configured_secret_nothing_is_accepted()
    {
        var provider = new StripeWebhookProvider(new WebhookOptions(), _h.Clock);

        Assert.Equal("webhook.not_configured", provider.Verify(new WebhookRequest("{}", BillingHarness.NoHeaders)).Error!.Code);
    }
}

public class OrderReferenceTests
{
    [Fact]
    public void A_reference_round_trips()
    {
        var user = Guid.NewGuid();

        Assert.True(OrderReference.TryParse(OrderReference.Format(user, "vip_monthly"), out var parsedUser, out var plan));
        Assert.Equal(user, parsedUser);
        Assert.Equal("vip_monthly", plan);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("vip_monthly")]
    [InlineData("not-a-guid:vip_monthly")]
    [InlineData("00000000-0000-0000-0000-000000000000:vip_monthly")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e:VIP MONTHLY")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e:a:b")]
    public void A_malformed_reference_is_rejected(string? value)
    {
        Assert.False(OrderReference.TryParse(value, out _, out _));
    }
}
