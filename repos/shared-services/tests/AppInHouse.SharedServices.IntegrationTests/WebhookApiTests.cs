using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>Telegram and Stripe have a secret in this host; GitHub has none.</summary>
public sealed class WebhookApiFactory : ApiFactory
{
    public const string TelegramSecret = "telegram-secret-token-0123456789";
    public const string StripeSecret = "stripe-signing-secret-0123456789";

    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["Webhooks:Inbound:telegram:Secret"] = TelegramSecret,
        ["Webhooks:Inbound:stripe:Secret"] = StripeSecret,
    };
}

/// <summary>A host whose applications are on the private network.</summary>
public sealed class PrivateWebhookApiFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["Webhooks:AllowPrivateNetworkHosts"] = "true",
        ["Webhooks:Inbound:github:Secret"] = "github-secret-0123456789",
    };
}

public class WebhookSubscriptionApiTests(WebhookApiFactory factory) : IClassFixture<WebhookApiFactory>
{
    private const string Subscriptions = "/api/v1/webhooks/subscriptions";

    private static string NewTarget() => $"https://hooks.example.test/{Guid.NewGuid():N}";

    private IEnumerable<(HttpRequestMessage Request, string Body)> CallsTo(string target) => factory.Webhook.Calls.ToArray().Where(c => c.Request.RequestUri!.ToString() == target);

    private static async Task<(JsonElement Subscription, string Secret)> CreateAsync(HttpClient client, string target, string[] events, string? project = null, string? secret = null)
    {
        var response = await client.PostAsJsonAsync(Subscriptions, new { name = "App " + Guid.NewGuid().ToString("N")[..6], targetUrl = target, events, projectCode = project, secretKey = secret });
        var body = await ReadAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body.ToString());
        return (body.GetProperty("subscription"), body.GetProperty("secretKey").GetString()!);
    }

    private async Task<string> SendEmailAsync(HttpClient client, string project)
    {
        var to = NewRecipient();
        var response = await client.PostAsJsonAsync("/api/v1/emails/send", new { to, projectCode = project, subject = "Hello", textBody = "Hi there" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return to;
    }

    private async Task<(HttpClient Admin, string Project)> ProjectWithMockEmailAsync()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, project);
        return (admin, project);
    }

    // ------------------------------------------------------------------------------------------------ the key

    [Fact]
    public async Task TheSigningKey_IsShownOnce_NeverListed_AndStoredOnlyProtected()
    {
        var admin = await AdminAsync(factory);
        var project = NewProject();

        var response = await admin.PostAsJsonAsync(Subscriptions, new { name = "App", targetUrl = NewTarget() + "?token=URL-TOKEN", events = new[] { "email.sent" }, projectCode = project });
        var created = await ReadAsync(response);
        var secret = created.GetProperty("secretKey").GetString()!;
        var id = created.GetProperty("subscription").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("whsec_", secret);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("••••••••" + secret[^4..], created.GetProperty("subscription").GetProperty("secretMask").GetString());

        var listed = await (await admin.GetAsync($"{Subscriptions}?projectCode={project}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, listed);
        var stored = await factory.WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id));
        Assert.DoesNotContain(secret, stored.ProtectedSecret);
        Assert.NotEqual(secret, stored.ProtectedSecret);

        var audit = await factory.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.ResourceId == id.ToString()).ToListAsync());
        var entry = Assert.Single(audit);
        Assert.Equal("CreateWebhookSubscription", entry.Action);
        Assert.DoesNotContain(secret, entry.DetailsJson);
        Assert.DoesNotContain("URL-TOKEN", entry.DetailsJson);   // the address may carry a token, so only the host is audited
        Assert.Contains("hooks.example.test", entry.DetailsJson);
    }

    [Fact]
    public async Task AnOwnKey_IsUsedAsGiven_ButOneUnderSixteenCharactersIsRefused()
    {
        var admin = await AdminAsync(factory);

        var (_, secret) = await CreateAsync(admin, NewTarget(), ["email.sent"], NewProject(), "my-own-signing-key-0123");
        var refused = await admin.PostAsJsonAsync(Subscriptions, new { name = "App", targetUrl = NewTarget(), events = new[] { "email.sent" }, secretKey = "short" });

        Assert.Equal("my-own-signing-key-0123", secret);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ delivery

    [Fact]
    public async Task AnEmailSent_IsPostedToTheSubscriber_SignedAndDescribed_AndCounted()
    {
        var (admin, project) = await ProjectWithMockEmailAsync();
        var target = NewTarget();
        var (subscription, secret) = await CreateAsync(admin, target, ["email.sent"], project);

        var to = await SendEmailAsync(admin, project);

        Assert.True(await UntilAsync(() => CallsTo(target).Any()), "nothing was posted to the subscriber");
        var (request, body) = Assert.Single(CallsTo(target));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        var signature = request.Headers.GetValues("X-AIH-Signature").Single();
        Assert.True(WebhookSigner.IsValid(secret, Encoding.UTF8.GetBytes(body), signature), "the signature does not match the body it came with");
        Assert.False(WebhookSigner.IsValid("another-secret-0123456789", Encoding.UTF8.GetBytes(body), signature));
        Assert.Equal("email.sent", request.Headers.GetValues("X-AIH-Event").Single());
        Assert.True(Guid.TryParse(request.Headers.GetValues("X-AIH-Delivery").Single(), out _));
        Assert.InRange(long.Parse(request.Headers.GetValues("X-AIH-Timestamp").Single()), DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        Assert.DoesNotContain(secret, body + string.Join(' ', request.Headers.SelectMany(h => h.Value)));

        using var json = JsonDocument.Parse(body);
        Assert.Equal(("email.sent", project), (json.RootElement.GetProperty("event").GetString(), json.RootElement.GetProperty("project").GetString()));
        Assert.Equal(request.Headers.GetValues("X-AIH-Delivery").Single(), json.RootElement.GetProperty("id").GetString());
        Assert.Equal(to, json.RootElement.GetProperty("data").GetProperty("recipient").GetString());
        Assert.DoesNotContain("Hi there", body);   // the message itself is not part of the event

        var id = subscription.GetProperty("id").GetGuid();
        Assert.True(await UntilAsync(async () => (await factory.WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id))).SuccessCount == 1));
        var row = await factory.WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id));
        Assert.Equal(("200", 0), (row.LastStatus, row.FailureCount));
        Assert.NotNull(row.LastDeliveryAt);
    }

    [Fact]
    public async Task AChatMessageSent_IsPostedAsChatSent_AndAFileUploadedAsFileUploaded()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateMockChatAsync(admin, project);
        await ActivateAsync(admin, project, "Storage", "LocalDisk", new { rootPath = "uploads" });
        var target = NewTarget();
        await CreateAsync(admin, target, ["chat.sent", "file.uploaded"], project);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/chat/send", new { text = "hello", projectCode = project })).StatusCode);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()) { Headers = { ContentType = new("image/png") } }, "file", "logo.png" }, { new StringContent(project), "projectCode" } };
        var uploaded = await ReadAsync(await admin.PostAsync("/api/v1/storage/upload", form));

        Assert.True(await UntilAsync(() => CallsTo(target).Count() == 2));
        var events = CallsTo(target).Select(c => JsonDocument.Parse(c.Body).RootElement.Clone()).ToList();
        Assert.Equal(["chat.sent", "file.uploaded"], events.Select(e => e.GetProperty("event").GetString()).Order());
        var file = events.Single(e => e.GetProperty("event").GetString() == "file.uploaded").GetProperty("data");
        Assert.Equal(uploaded.GetProperty("file").GetProperty("id").GetGuid(), file.GetProperty("fileId").GetGuid());
        Assert.Equal(("logo.png", "image/png"), (file.GetProperty("fileName").GetString(), file.GetProperty("mimeType").GetString()));
        Assert.DoesNotContain("hello", string.Concat(CallsTo(target).Select(c => c.Body)));
    }

    [Fact]
    public async Task AnApplication_HearsOnlyTheEventsItAskedFor_FromItsOwnProject_ThePlatformHearsEveryProject()
    {
        var (admin, projectA) = await ProjectWithMockEmailAsync();
        var projectB = NewProject();
        await ActivateMockEmailAsync(admin, projectB);
        var (targetA, targetB, targetAll, targetChat) = (NewTarget(), NewTarget(), NewTarget(), NewTarget());
        await CreateAsync(admin, targetA, ["email.sent"], projectA);
        await CreateAsync(admin, targetB, ["email.sent"], projectB);
        await CreateAsync(admin, targetAll, ["*"], "default");
        await CreateAsync(admin, targetChat, ["chat.sent"], projectA);

        await SendEmailAsync(admin, projectA);

        Assert.True(await UntilAsync(() => CallsTo(targetA).Any() && CallsTo(targetAll).Any()));
        await Task.Delay(300);   // time for a wrong delivery to show up
        Assert.Single(CallsTo(targetA));
        Assert.Single(CallsTo(targetAll));
        Assert.Empty(CallsTo(targetB));      // another project's application
        Assert.Empty(CallsTo(targetChat));   // an event it did not ask for

        await SendEmailAsync(admin, projectB);
        Assert.True(await UntilAsync(() => CallsTo(targetB).Any() && CallsTo(targetAll).Count() == 2));
        Assert.Single(CallsTo(targetA));
    }

    [Fact]
    public async Task ADeletedSubscription_HearsNothingMore()
    {
        var (admin, project) = await ProjectWithMockEmailAsync();
        var target = NewTarget();
        var (subscription, _) = await CreateAsync(admin, target, ["email.sent"], project);
        await SendEmailAsync(admin, project);
        Assert.True(await UntilAsync(() => CallsTo(target).Any()));

        var deleted = await admin.DeleteAsync($"{Subscriptions}/{subscription.GetProperty("id").GetGuid()}");
        await SendEmailAsync(admin, project);
        await Task.Delay(400);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Single(CallsTo(target));
        Assert.Empty((await ReadAsync(await admin.GetAsync($"{Subscriptions}?projectCode={project}"))).EnumerateArray());
    }

    [Fact]
    public async Task ADeliveryThatFailsOnTheirSide_IsRetried_UntilTheApplicationAnswersOk()
    {
        var (admin, project) = await ProjectWithMockEmailAsync();
        var target = NewTarget();
        var (subscription, _) = await CreateAsync(admin, target, ["email.sent"], project);
        var attempts = 0;
        factory.Webhook.Respond = request => request.RequestUri!.ToString() == target && Interlocked.Increment(ref attempts) <= 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK);
        try
        {
            await SendEmailAsync(admin, project);

            var id = subscription.GetProperty("id").GetGuid();
            Assert.True(await UntilAsync(async () => (await factory.WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id))).SuccessCount == 1));
            var row = await factory.WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id));
            Assert.Equal((1, 2, "200"), (row.SuccessCount, row.FailureCount, row.LastStatus));
            Assert.Equal(3, CallsTo(target).Count());
            // The same event, with the same id, each time: the application can tell it is a repeat.
            Assert.Single(CallsTo(target).Select(c => c.Request.Headers.GetValues("X-AIH-Delivery").Single()).Distinct());
        }
        finally
        {
            factory.Webhook.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AnAnswerOf400_IsNotRetried_ItWouldNeverChange()
    {
        var (admin, project) = await ProjectWithMockEmailAsync();
        var target = NewTarget();
        var (subscription, _) = await CreateAsync(admin, target, ["email.sent"], project);
        factory.Webhook.Respond = request => new HttpResponseMessage(request.RequestUri!.ToString() == target ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        try
        {
            await SendEmailAsync(admin, project);

            var subscriptionId = subscription.GetProperty("id").GetString()!;
            Assert.True(await UntilAsync(() => factory.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().AnyAsync(j => j.Queue == "webhook" && j.Status == JobStatus.Failed && j.PayloadJson.Contains(subscriptionId)))));
            await Task.Delay(300);
            Assert.Single(CallsTo(target));
            var job = await factory.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.PayloadJson.Contains(subscriptionId)));
            Assert.Equal(1, job.Attempts);
            Assert.Contains("400", job.LastError);
        }
        finally
        {
            factory.Webhook.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task TheTestAction_PostsASignedTestEvent_AndReportsWhatTheApplicationAnswered()
    {
        var (admin, project) = await ProjectWithMockEmailAsync();
        var target = NewTarget();
        var (subscription, secret) = await CreateAsync(admin, target, ["email.sent"], project);
        var id = subscription.GetProperty("id").GetGuid();

        var ok = await ReadAsync(await admin.PostAsync($"{Subscriptions}/{id}/test", null));
        factory.Webhook.Respond = request => new HttpResponseMessage(request.RequestUri!.ToString() == target ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        JsonElement failed;
        try
        {
            failed = await ReadAsync(await admin.PostAsync($"{Subscriptions}/{id}/test", null));
        }
        finally
        {
            factory.Webhook.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        }

        Assert.True(ok.GetProperty("success").GetBoolean());
        Assert.Equal(200, ok.GetProperty("statusCode").GetInt32());
        Assert.False(failed.GetProperty("success").GetBoolean());
        Assert.Equal(500, failed.GetProperty("statusCode").GetInt32());
        var (request, body) = CallsTo(target).First();
        Assert.Equal("webhook.test", request.Headers.GetValues("X-AIH-Event").Single());
        Assert.True(WebhookSigner.IsValid(secret, Encoding.UTF8.GetBytes(body), request.Headers.GetValues("X-AIH-Signature").Single()));
        var row = await factory.WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id));
        Assert.Equal((1, 1), (row.SuccessCount, row.FailureCount));
    }

    // ------------------------------------------------------------------------------------------------ what is refused

    [Theory]
    [InlineData("ftp://hooks.example.test/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("hooks.example.test/x")]
    [InlineData("https://user:password@hooks.example.test/x")]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://10.0.0.5/x")]
    [InlineData("http://192.168.1.200:5001/x")]
    [InlineData("http://172.16.0.9/x")]
    [InlineData("http://[::1]/x")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://0.0.0.0/x")]
    public async Task ATargetThatIsNotAnOutsideHttpAddress_IsRefused_SoTheServiceCannotBePointedAtItsOwnNetwork(string target)
    {
        var admin = await AdminAsync(factory);

        var response = await admin.PostAsJsonAsync(Subscriptions, new { name = "Bad", targetUrl = target, events = new[] { "email.sent" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await factory.WithDbAsync(db => db.WebhookSubscriptions.Where(s => s.TargetUrl == target).ToListAsync()));
    }

    public static TheoryData<string[]> BadEvents => new() { new string[0], new[] { "user.deleted" }, new[] { " " } };

    [Theory]
    [MemberData(nameof(BadEvents))]
    public async Task NoEventOrAnUnknownOne_IsRefused(string[] events)
    {
        var admin = await AdminAsync(factory);

        var response = await admin.PostAsJsonAsync(Subscriptions, new { name = "Bad", targetUrl = NewTarget(), events });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AProjectKey_ManagesOnlyItsOwnSubscriptions()
    {
        var mine = NewProject();
        var other = NewProject();
        var admin = await AdminAsync(factory);
        var owner = await ProjectClientAsync(factory, mine, ApiScopes.ConfigRead, ApiScopes.ConfigWrite);
        var (foreign, _) = await CreateAsync(admin, NewTarget(), ["email.sent"], other);
        var (own, _) = await CreateAsync(owner, NewTarget(), ["email.sent"]);   // no project given: its own

        Assert.Equal(mine, own.GetProperty("projectCode").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(Subscriptions, new { name = "x", targetUrl = NewTarget(), events = new[] { "email.sent" }, projectCode = other })).StatusCode);
        var listed = (await ReadAsync(await owner.GetAsync(Subscriptions))).EnumerateArray().Select(s => s.GetProperty("projectCode").GetString()).ToList();
        Assert.All(listed, p => Assert.Equal(mine, p));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{Subscriptions}?projectCode={other}")).StatusCode);
        var foreignId = foreign.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"{Subscriptions}/{foreignId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync($"{Subscriptions}/{foreignId}/test", null)).StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.WebhookSubscriptions.AnyAsync(s => s.Id == foreignId)));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{Subscriptions}/{own.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [Fact]
    public async Task WithoutTheWriteScope_ASubscriptionCannotBeMadeOrDeleted_AndAnUnknownOneIs404()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        var reader = await ProjectClientAsync(factory, project, ApiScopes.ConfigRead);
        var (subscription, _) = await CreateAsync(admin, NewTarget(), ["email.sent"], project);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Subscriptions, new { name = "x", targetUrl = NewTarget(), events = new[] { "email.sent" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{Subscriptions}/{subscription.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Subscriptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync(Subscriptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Subscriptions}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{Subscriptions}/{Guid.NewGuid()}/test", null)).StatusCode);
    }
}

public class PrivateWebhookTargetTests(PrivateWebhookApiFactory factory) : IClassFixture<PrivateWebhookApiFactory>
{
    [Theory]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://192.168.1.200:5020/hook")]
    [InlineData("http://127.0.0.1:9/hook")]
    public async Task OnAPrivateNetworkInstallation_TheApplicationsOfTheNetworkCanSubscribe(string target)
    {
        var admin = await AdminAsync(factory);

        var response = await admin.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new { name = "Internal app", targetUrl = target, events = new[] { "email.sent" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]   // the cloud metadata address stays closed whatever the switch says
    [InlineData("http://0.0.0.0/hook")]
    [InlineData("http://[fe80::1]/hook")]
    public async Task ButTheLinkLocalAndMetadataAddresses_StayRefused(string target)
    {
        var admin = await AdminAsync(factory);

        var response = await admin.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new { name = "Bad", targetUrl = target, events = new[] { "email.sent" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AGitHubCall_WithTheRightHmacOfItsBody_IsAcceptedAndQueued_AWrongOneIsNot()
    {
        var client = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main","repository":{"name":"app"}}""");

        var good = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/incoming/github") { Content = new ByteArrayContent(body) };
        good.Headers.Add("X-Hub-Signature-256", WebhookSigner.Sign("github-secret-0123456789", body));
        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/incoming/github") { Content = new ByteArrayContent(body) };
        bad.Headers.Add("X-Hub-Signature-256", WebhookSigner.Sign("not-the-secret-0123456789", body));

        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(good)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bad)).StatusCode);
        Assert.True(await UntilAsync(async () => await factory.WithDbAsync(db => db.InboundWebhookLogs.AnyAsync(l => l.Source == "github" && l.Status == InboundWebhookStatus.Processed))));
    }
}

public class InboundWebhookApiTests(WebhookApiFactory factory) : IClassFixture<WebhookApiFactory>
{
    private const string Telegram = "/api/v1/webhooks/incoming/telegram";

    private static HttpRequestMessage Telegram_(string? token, string body = """{"update_id":7,"message":{"text":"hello"}}""")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Telegram) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", token);
        return request;
    }

    private static HttpRequestMessage Stripe(string body, DateTimeOffset at, string secret = WebhookApiFactory.StripeSecret)
    {
        var t = at.ToUnixTimeSeconds();
        var hmac = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/incoming/stripe") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", $"t={t},v1={hmac}");
        return request;
    }

    [Fact]
    public async Task ACallWithTheRightSecretToken_NeedsNoApiKey_IsAcceptedAnd202_StoredAndProcessedInTheBackground()
    {
        var response = await factory.CreateClient().SendAsync(Telegram_(WebhookApiFactory.TelegramSecret));
        var body = await ReadAsync(response);
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(body.GetProperty("accepted").GetBoolean());
        var log = await factory.WithDbAsync(db => db.InboundWebhookLogs.AsNoTracking().SingleAsync(l => l.Id == id));
        Assert.Equal(("telegram", true), (log.Source, log.SignatureValid));
        Assert.Contains("\"update_id\":7", log.Payload);
        Assert.True(await UntilAsync(async () => (await factory.WithDbAsync(db => db.InboundWebhookLogs.AsNoTracking().SingleAsync(l => l.Id == id))).Status == InboundWebhookStatus.Processed));
        Assert.NotNull((await factory.WithDbAsync(db => db.InboundWebhookLogs.AsNoTracking().SingleAsync(l => l.Id == id))).ProcessedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("guess")]
    [InlineData("telegram-secret-token-012345678")]
    public async Task AMissingOrWrongToken_Is401_AndTheRefusalIsLoggedWithoutTheBody(string? token)
    {
        var before = await factory.WithDbAsync(db => db.InboundWebhookLogs.CountAsync(l => l.Status == InboundWebhookStatus.Rejected));

        var response = await factory.CreateClient().SendAsync(Telegram_(token, """{"update_id":999,"secret":"SENSITIVE-BODY"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("SENSITIVE-BODY", await response.Content.ReadAsStringAsync());
        var rejected = await factory.WithDbAsync(db => db.InboundWebhookLogs.AsNoTracking().Where(l => l.Status == InboundWebhookStatus.Rejected).ToListAsync());
        Assert.Equal(before + 1, rejected.Count);
        Assert.All(rejected, l => Assert.Null(l.Payload));
        Assert.DoesNotContain(await factory.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().Select(j => j.PayloadJson).ToListAsync()), p => p.Contains("SENSITIVE-BODY"));
    }

    [Fact]
    public async Task ASourceThatIsKnownButHasNoSecretHere_RefusesEverything_AnUnknownOneIs404()
    {
        var client = factory.CreateClient();
        var github = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/incoming/github") { Content = new StringContent("{}") };
        github.Headers.Add("X-Hub-Signature-256", WebhookSigner.Sign("", "{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(github)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/webhooks/incoming/paypal", new StringContent("{}"))).StatusCode);
        // The name of a source is not case-sensitive, and it still needs the secret: no way around the check by spelling.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/webhooks/incoming/TELEGRAM", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task ABodyOverTwoHundredFiftySixKilobytes_Is413_WithoutBeingStored()
    {
        var before = await factory.WithDbAsync(db => db.InboundWebhookLogs.CountAsync());

        var response = await factory.CreateClient().SendAsync(Telegram_(WebhookApiFactory.TelegramSecret, new string('x', InboundSources.MaxBodyBytes + 1)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(before, await factory.WithDbAsync(db => db.InboundWebhookLogs.CountAsync()));
    }

    [Fact]
    public async Task AStripeCall_NeedsAFreshTimestampAndTheHmacOfTimestampAndBody()
    {
        var client = factory.CreateClient();
        const string body = """{"id":"evt_1","type":"payment_intent.succeeded"}""";

        var fresh = await client.SendAsync(Stripe(body, DateTimeOffset.UtcNow));
        var stale = await client.SendAsync(Stripe(body, DateTimeOffset.UtcNow.AddMinutes(-10)));
        var wrongSecret = await client.SendAsync(Stripe(body, DateTimeOffset.UtcNow, "another-secret-0123456789"));
        var tampered = Stripe(body, DateTimeOffset.UtcNow);
        tampered.Content = new StringContent(body.Replace("evt_1", "evt_2"), Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.Accepted, fresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(tampered)).StatusCode);
    }

    [Fact]
    public async Task AnAcceptedCall_IsHandedOnToTheApplicationsThatSubscribedToWebhookReceived()
    {
        var admin = await AdminAsync(factory);
        var target = $"https://hooks.example.test/{Guid.NewGuid():N}";
        var created = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/webhooks/subscriptions", new { name = "Bot", targetUrl = target, events = new[] { "webhook.received" }, projectCode = "default" }));
        var secret = created.GetProperty("secretKey").GetString()!;

        await factory.CreateClient().SendAsync(Telegram_(WebhookApiFactory.TelegramSecret, """{"update_id":31337}"""));

        Assert.True(await UntilAsync(() => factory.Webhook.Calls.ToArray().Any(c => c.Request.RequestUri!.ToString() == target)));
        var (request, body) = factory.Webhook.Calls.ToArray().First(c => c.Request.RequestUri!.ToString() == target);
        Assert.True(WebhookSigner.IsValid(secret, Encoding.UTF8.GetBytes(body), request.Headers.GetValues("X-AIH-Signature").Single()));
        using var json = JsonDocument.Parse(body);
        Assert.Equal("webhook.received", json.RootElement.GetProperty("event").GetString());
        Assert.Equal("telegram", json.RootElement.GetProperty("data").GetProperty("source").GetString());
        Assert.Contains("31337", json.RootElement.GetProperty("data").GetProperty("payload").GetString());
    }

    [Fact]
    public async Task TheSourcesList_ShowsAddressMethodAndWhetherASecretIsSet_AndCountsTheLast24Hours_ForThePlatformOnly()
    {
        var admin = await AdminAsync(factory);
        await factory.CreateClient().SendAsync(Telegram_(WebhookApiFactory.TelegramSecret));
        await factory.CreateClient().SendAsync(Telegram_("wrong"));

        var sources = (await ReadAsync(await admin.GetAsync("/api/v1/webhooks/inbound"))).EnumerateArray().ToList();

        Assert.Equal(["telegram", "github", "stripe"], sources.Select(s => s.GetProperty("source").GetString()));
        var telegram = sources[0];
        Assert.Equal("/api/v1/webhooks/incoming/telegram", telegram.GetProperty("path").GetString());
        Assert.True(telegram.GetProperty("configured").GetBoolean());
        Assert.False(sources[1].GetProperty("configured").GetBoolean());
        Assert.True(telegram.GetProperty("received24h").GetInt32() >= 1);
        Assert.True(telegram.GetProperty("rejected24h").GetInt32() >= 1);
        Assert.NotEqual(JsonValueKind.Null, telegram.GetProperty("lastReceivedAt").ValueKind);
        Assert.DoesNotContain(WebhookApiFactory.TelegramSecret, await (await admin.GetAsync("/api/v1/webhooks/inbound")).Content.ReadAsStringAsync());

        var project = await ProjectClientAsync(factory, NewProject(), ApiScopes.ConfigRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await project.GetAsync("/api/v1/webhooks/inbound")).StatusCode);
    }
}
