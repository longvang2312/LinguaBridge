using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

public class NotificationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Dispatch = "/api/v1/notifications/dispatch";
    private const string Templates = "/api/v1/notifications/templates";
    private const string Logs = "/api/v1/notifications/logs";

    private static object Registered(string email, string? project = null, object? data = null, bool? queue = null, string? priority = null, string template = "UserRegistered") => new
    {
        templateCode = template, projectCode = project, recipientEmail = email, queue, priority,
        data = data ?? Data(new { UserName = "Lan", AppName = "LinguaBridge", ActionUrl = "https://app.example.com/verify?t=abc&u=1", ExpiryMinutes = 30 }),
    };

    // ------------------------------------------------------------------------------------------------ templates

    [Fact]
    public async Task TheFiveStockTemplates_AreThere_WithThePlaceholdersEachOneNeeds()
    {
        var admin = await AdminAsync(factory);

        var response = await admin.GetAsync(Templates);
        var templates = (await ReadAsync(response)).EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["PasswordReset", "PaymentSuccess", "SecurityAlert", "SystemMaintenance", "UserRegistered"], templates.Select(t => t.GetProperty("code").GetString()));
        var registered = templates.Single(t => t.GetProperty("code").GetString() == "UserRegistered");
        Assert.Equal(["UserName", "AppName", "ActionUrl", "ExpiryMinutes"], registered.GetProperty("placeholders").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("Email", registered.GetProperty("defaultChannel").GetString());
        Assert.Equal("Critical", templates.Single(t => t.GetProperty("code").GetString() == "SecurityAlert").GetProperty("priority").GetString());
        Assert.Equal("Both", templates.Single(t => t.GetProperty("code").GetString() == "SecurityAlert").GetProperty("defaultChannel").GetString());
    }

    // ------------------------------------------------------------------------------------------------ dispatching

    [Fact]
    public async Task ADispatch_FillsTheTemplate_AndDeliversThroughTheRealPipeline_Logged()
    {
        var project = NewProject();
        var to = NewRecipient();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ConfigRead);

        var response = await client.PostAsJsonAsync(Dispatch, Registered(to));
        var body = await ReadAsync(response);

        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.Equal(("Sent", true), (body.GetProperty("status").GetString(), body.GetProperty("success").GetBoolean()));
        var mail = Assert.Single(factory.MockMail.SentMessages, m => m.To == to);
        Assert.Equal("Chào mừng Lan đến với LinguaBridge", mail.Subject);
        Assert.Contains("Liên kết có hiệu lực trong 30 phút.", mail.TextBody);
        Assert.Contains("<a href=\"https://app.example.com/verify?t=abc&amp;u=1\">", mail.HtmlBody);

        var logs = await ReadAsync(await client.GetAsync($"{Logs}?limit=5"));
        var log = Assert.Single(logs.EnumerateArray());
        Assert.Equal((project, "UserRegistered", "Email", "Sent", to), (log.GetProperty("projectCode").GetString(), log.GetProperty("templateCode").GetString(), log.GetProperty("channels").GetString(), log.GetProperty("status").GetString(), log.GetProperty("recipient").GetString()));
        Assert.Equal(1, await factory.WithDbAsync(db => db.DeliveryLogs.CountAsync(l => l.ProjectCode == project && l.Recipient == to)));
    }

    [Fact]
    public async Task NumbersAndBooleansInData_FillThePlaceholders_ButANestedObjectIsRefused()
    {
        var project = NewProject();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);

        var numbers = await client.PostAsJsonAsync(Dispatch, Registered(NewRecipient(), data: Data(new { UserName = "Lan", AppName = true, ActionUrl = "https://a.example.com", ExpiryMinutes = 45.5 })));
        var nested = await client.PostAsJsonAsync(Dispatch, Registered(NewRecipient(), data: Data(new { UserName = new { first = "Lan" }, AppName = "x", ActionUrl = "u", ExpiryMinutes = 1 })));
        var notAnObject = await client.PostAsJsonAsync(Dispatch, new { templateCode = "UserRegistered", recipientEmail = NewRecipient(), data = new[] { 1, 2 } });

        Assert.Equal(HttpStatusCode.OK, numbers.StatusCode);
        Assert.Contains(factory.MockMail.SentMessages, m => m.Subject == "Chào mừng Lan đến với true" && m.TextBody!.Contains("45.5 phút"));
        Assert.Equal(HttpStatusCode.BadRequest, nested.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAnObject.StatusCode);
    }

    [Fact]
    public async Task AMissingValue_IsA400NamingIt_AnUnknownTemplateA404_AndABadAddressA400_NothingIsSentOrLogged()
    {
        var project = NewProject();
        var to = NewRecipient();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ConfigRead);

        var missing = await client.PostAsJsonAsync(Dispatch, Registered(to, data: Data(new { UserName = "Lan" })));
        var unknown = await client.PostAsJsonAsync(Dispatch, Registered(to, template: "NoSuchTemplate"));
        var bad = await client.PostAsJsonAsync(Dispatch, Registered("not-an-address"));
        var noEmail = await client.PostAsJsonAsync(Dispatch, new { templateCode = "UserRegistered", data = Data(new { UserName = "a", AppName = "b", ActionUrl = "c", ExpiryMinutes = 1 }) });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("ActionUrl", await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noEmail.StatusCode);
        Assert.DoesNotContain(factory.MockMail.SentMessages, m => m.To == to);
        Assert.Empty((await ReadAsync(await client.GetAsync(Logs))).EnumerateArray());
    }

    [Fact]
    public async Task WithoutAKey_401_WithoutAnyScopeOfTheTemplatesChannel_403()
    {
        var project = NewProject();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).PostAsJsonAsync(Dispatch, Registered(NewRecipient()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ProjectClientAsync(factory, project, ApiScopes.StorageRead, ApiScopes.ConfigRead)).PostAsJsonAsync(Dispatch, Registered(NewRecipient()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await ProjectClientAsync(factory, project, ApiScopes.EmailSend)).PostAsJsonAsync(Dispatch, Registered(NewRecipient()))).StatusCode);
    }

    [Fact]
    public async Task AKeyWithOnlyTheChatScope_CannotSendAnEmailTemplate_NothingIsSentOrLogged_WhicheverWayItIsAsked()
    {
        var project = NewProject();
        var victim = NewRecipient();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, project);
        var chatOnly = await ProjectClientAsync(factory, project, ApiScopes.ChatSend);
        var telegramOnly = await ProjectClientAsync(factory, project, ApiScopes.TelegramSend);

        var now = await chatOnly.PostAsJsonAsync(Dispatch, Registered(victim, data: Data(new { UserName = "x", AppName = "y", ActionUrl = "https://evil.example/login", ExpiryMinutes = 1 })));
        var queued = await chatOnly.PostAsJsonAsync(Dispatch, Registered(victim, queue: true));
        var viaTelegram = await telegramOnly.PostAsJsonAsync(Dispatch, Registered(victim));
        var direct = await chatOnly.PostAsJsonAsync("/api/v1/emails/send", new { to = victim, subject = "s", textBody = "t" });

        Assert.Equal(HttpStatusCode.Forbidden, now.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, queued.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viaTelegram.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);   // the same answer as sending the email directly
        Assert.DoesNotContain(factory.MockMail.SentMessages, m => m.To == victim);
        Assert.Equal(0, await factory.WithDbAsync(db => db.DeliveryLogs.CountAsync(l => l.ProjectCode == project)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.BackgroundJobs.CountAsync(j => j.PayloadJson.Contains(victim))));
        Assert.Empty((await ReadAsync(await admin.GetAsync($"{Logs}?projectCode={project}"))).EnumerateArray());
    }

    [Fact]
    public async Task AKeyWithOnlyTheEmailScope_CannotSendAChatOrBothTemplate()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, project);
        await ActivateMockChatAsync(admin, project);
        var emailOnly = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);
        var alert = new
        {
            templateCode = "SecurityAlert", recipientEmail = NewRecipient(),
            data = Data(new { UserName = "Lan", Details = "d", ActionUrl = "https://app.example.com/security" }),
        };

        var both = await emailOnly.PostAsJsonAsync(Dispatch, alert);
        var bothQueued = await emailOnly.PostAsJsonAsync(Dispatch, new { alert.templateCode, alert.recipientEmail, alert.data, queue = true });

        Assert.Equal(HttpStatusCode.Forbidden, both.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bothQueued.StatusCode);
        Assert.DoesNotContain(factory.MockMail.SentMessages, m => m.To == alert.recipientEmail);
        Assert.DoesNotContain(MockChat(factory).SentMessages, m => m.ProjectCode == project);
    }

    [Fact]
    public async Task ACriticalEmailThatFails_DoesNotFallBackToChat_WhenTheKeyHasNoChatScope()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateSendGridAsync(admin, project);
        await ActivateMockChatAsync(admin, project);
        var emailOnly = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);
        factory.SendGrid.Respond = _ => ServerError();
        try
        {
            var response = await emailOnly.PostAsJsonAsync(Dispatch, Registered(NewRecipient(), priority: "Critical"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.DoesNotContain(MockChat(factory).SentMessages, m => m.ProjectCode == project);
        }
        finally
        {
            factory.SendGrid.Respond = _ => Accepted();
        }
    }

    [Fact]
    public async Task AKeyOfOneProject_CannotSendForAnother_ThePlatformCan()
    {
        var mine = NewProject();
        var other = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, other);
        var client = await ProjectClientAsync(factory, mine, ApiScopes.EmailSend);

        var refused = await client.PostAsJsonAsync(Dispatch, Registered(NewRecipient(), project: other));
        var allowed = await admin.PostAsJsonAsync(Dispatch, Registered(NewRecipient(), project: other));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ABothTemplate_DeliversByEmailAndChat_ToTheGivenChat()
    {
        var project = NewProject();
        var to = NewRecipient();
        var chatId = "chat-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, project);
        await ActivateMockChatAsync(admin, project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ChatSend);

        var response = await client.PostAsJsonAsync(Dispatch, new
        {
            templateCode = "SecurityAlert", recipientEmail = to, recipientChatId = chatId,
            data = Data(new { UserName = "Lan", Details = "login from a new device", ActionUrl = "https://app.example.com/security" }),
        });
        var body = await ReadAsync(response);

        Assert.Equal("Sent", body.GetProperty("status").GetString());
        Assert.Contains(factory.MockMail.SentMessages, m => m.To == to && m.Subject == "Cảnh báo bảo mật cho tài khoản Lan");
        var chat = Assert.Single(MockChat(factory).SentMessages, m => m.Recipient == chatId);
        Assert.Contains("login from a new device", chat.Text);
        Assert.Equal(["Email", "Chat"], body.GetProperty("channels").EnumerateArray().Select(c => c.GetProperty("channel").GetString()));
    }

    // ------------------------------------------------------------------------------------------------ fallback and failure

    [Fact]
    public async Task WhenTheEmailProviderIsDown_ACriticalNotificationFallsBackToChat_WithASummaryOnly()
    {
        var project = NewProject();
        var to = NewRecipient();
        var admin = await AdminAsync(factory);
        await ActivateSendGridAsync(admin, project);
        await ActivateMockChatAsync(admin, project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ChatSend, ApiScopes.ConfigRead);
        factory.SendGrid.Respond = _ => ServerError();
        try
        {
            var response = await client.PostAsJsonAsync(Dispatch, Registered(to, priority: "Critical", data: Data(new { UserName = "Lan", AppName = "App", ActionUrl = "https://app.example.com/verify?token=ONE-TIME-TOKEN", ExpiryMinutes = 10 })));
            var body = await ReadAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Fallback", body.GetProperty("status").GetString());
            var channels = body.GetProperty("channels").EnumerateArray().ToList();
            Assert.Equal(("Email", false, false), (channels[0].GetProperty("channel").GetString(), channels[0].GetProperty("success").GetBoolean(), channels[0].GetProperty("isFallback").GetBoolean()));
            Assert.Equal(("Chat", true, true), (channels[1].GetProperty("channel").GetString(), channels[1].GetProperty("success").GetBoolean(), channels[1].GetProperty("isFallback").GetBoolean()));

            var chat = Assert.Single(MockChat(factory).SentMessages, m => m.ProjectCode == project);
            Assert.Contains("Chào mừng Lan", chat.Text);
            Assert.DoesNotContain("ONE-TIME-TOKEN", chat.Text);
            Assert.DoesNotContain(to, chat.Text);

            var deliveries = await factory.WithDbAsync(db => db.DeliveryLogs.Where(l => l.ProjectCode == project).OrderBy(l => l.CreatedAt).ToListAsync());
            Assert.Equal([(ChannelType.Email, DeliveryStatus.Failed), (ChannelType.Chat, DeliveryStatus.Sent)], deliveries.Select(l => (l.ChannelType, l.Status)));
            var log = (await ReadAsync(await client.GetAsync(Logs))).EnumerateArray().Single();
            Assert.Equal(("Email→Chat (fallback)", "Fallback"), (log.GetProperty("channels").GetString(), log.GetProperty("status").GetString()));
        }
        finally
        {
            factory.SendGrid.Respond = _ => Accepted();
        }
    }

    [Fact]
    public async Task ANormalNotificationWhoseOnlyChannelFails_IsA502_WithTheReasonAndTheLogId_AndNoFallback()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateSendGridAsync(admin, project);
        await ActivateMockChatAsync(admin, project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ChatSend, ApiScopes.ConfigRead);
        factory.SendGrid.Respond = _ => ServerError();
        try
        {
            var response = await client.PostAsJsonAsync(Dispatch, Registered(NewRecipient()));
            var problem = await ReadAsync(response);

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Contains("Email:", problem.GetProperty("detail").GetString());
            Assert.True(problem.TryGetProperty("logId", out _));
            Assert.DoesNotContain(MockChat(factory).SentMessages, m => m.ProjectCode == project);
            Assert.Equal("Failed", (await ReadAsync(await client.GetAsync(Logs))).EnumerateArray().Single().GetProperty("status").GetString());
        }
        finally
        {
            factory.SendGrid.Respond = _ => Accepted();
        }
    }

    [Fact]
    public async Task AProjectWithNoEmailConfiguration_GetsA502_NotACrash()
    {
        var client = await ProjectClientAsync(factory, NewProject(), ApiScopes.EmailSend);

        var response = await client.PostAsJsonAsync(Dispatch, Registered(NewRecipient()));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ the queue

    [Fact]
    public async Task InQueueMode_TheAnswerIs202_AndTheBackgroundWorkerDeliversTheEmail()
    {
        var project = NewProject();
        var to = NewRecipient();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ConfigRead);

        var response = await client.PostAsJsonAsync(Dispatch, Registered(to, queue: true));
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        var jobId = Assert.Single(body.GetProperty("jobIds").EnumerateArray()).GetGuid();
        Assert.True(await UntilAsync(() => factory.MockMail.SentMessages.Any(m => m.To == to)), "the worker never sent the email");
        Assert.True(await UntilAsync(async () => (await factory.WithDbAsync(db => db.BackgroundJobs.SingleAsync(j => j.Id == jobId))).Status == JobStatus.Completed));
        Assert.Equal("Queued", (await ReadAsync(await client.GetAsync(Logs))).EnumerateArray().Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task AQueuedEmail_WhoseProviderFailsTwice_IsDeliveredOnTheThirdAttempt()
    {
        var project = NewProject();
        var to = NewRecipient();
        var admin = await AdminAsync(factory);
        await ActivateSendGridAsync(admin, project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);
        var calls = 0;
        factory.SendGrid.Respond = _ => Interlocked.Increment(ref calls) <= 2 ? ServerError() : Accepted();
        try
        {
            var body = await ReadAsync(await client.PostAsJsonAsync(Dispatch, Registered(to, queue: true)));
            var jobId = body.GetProperty("jobIds")[0].GetGuid();

            Assert.True(await UntilAsync(async () => (await factory.WithDbAsync(db => db.BackgroundJobs.SingleAsync(j => j.Id == jobId))).Status == JobStatus.Completed));

            var job = await factory.WithDbAsync(db => db.BackgroundJobs.SingleAsync(j => j.Id == jobId));
            Assert.Equal((3, 3), (job.Attempts, calls));
            Assert.Null(job.LastError);
            var deliveries = await factory.WithDbAsync(db => db.DeliveryLogs.Where(l => l.ProjectCode == project).Select(l => l.Status).ToListAsync());
            Assert.Equal(2, deliveries.Count(s => s == DeliveryStatus.Failed));
            Assert.Single(deliveries, s => s == DeliveryStatus.Sent);
        }
        finally
        {
            factory.SendGrid.Respond = _ => Accepted();
        }
    }

    [Fact]
    public async Task AQueuedChatMessage_IsDeliveredByTheChatWorker_ToTheGivenChat()
    {
        var project = NewProject();
        var chatId = "chat-" + Guid.NewGuid().ToString("N")[..8];
        await ActivateMockChatAsync(await AdminAsync(factory), project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.ChatSend, ApiScopes.EmailSend);
        // A "Both" template needs an email address too, but without an email configuration its email job fails for good and the chat one still goes.
        var response = await client.PostAsJsonAsync(Dispatch, new
        {
            templateCode = "SystemMaintenance", recipientEmail = NewRecipient(), recipientChatId = chatId, queue = true,
            data = Data(new { AppName = "LinguaBridge", StartsAt = "22:00", DurationMinutes = 30 }),
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(await UntilAsync(() => MockChat(factory).SentMessages.Any(m => m.Recipient == chatId)));
    }

    // ------------------------------------------------------------------------------------------------ quotas

    [Fact]
    public async Task ADailyQuota_StopsTheNextDispatch_With429_RetryAfter_AndTheQuotaInTheBody()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, project);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/projects", new { projectCode = project, name = "Quota", dailyEmailQuota = 1 })).StatusCode);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ConfigRead);

        var first = await client.PostAsJsonAsync(Dispatch, Registered(NewRecipient()));
        var second = await client.PostAsJsonAsync(Dispatch, Registered(NewRecipient()));
        var problem = await ReadAsync(second);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("Daily quota exceeded", problem.GetProperty("title").GetString());
        Assert.Equal((1, 1), (problem.GetProperty("quota").GetInt32(), problem.GetProperty("used").GetInt32()));
        Assert.True(second.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.Single()) is > 0 and <= 86400);
        Assert.Equal(["Failed", "Sent"], (await ReadAsync(await client.GetAsync(Logs))).EnumerateArray().Select(l => l.GetProperty("status").GetString()).Order());
    }

    // ------------------------------------------------------------------------------------------------ logs

    [Fact]
    public async Task TheLogs_OfAProjectKey_AreOnlyItsOwn_ThePlatformSeesAll_AndAnotherProjectIs403()
    {
        var a = NewProject();
        var b = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, a);
        await ActivateMockEmailAsync(admin, b);
        var clientA = await ProjectClientAsync(factory, a, ApiScopes.EmailSend, ApiScopes.ConfigRead);
        await clientA.PostAsJsonAsync(Dispatch, Registered(NewRecipient()));
        await admin.PostAsJsonAsync(Dispatch, Registered(NewRecipient(), project: b));

        var own = (await ReadAsync(await clientA.GetAsync(Logs))).EnumerateArray().ToList();
        var forbidden = await clientA.GetAsync($"{Logs}?projectCode={b}");
        var platform = (await ReadAsync(await admin.GetAsync($"{Logs}?limit=100"))).EnumerateArray().Select(l => l.GetProperty("projectCode").GetString()).ToList();

        Assert.All(own, l => Assert.Equal(a, l.GetProperty("projectCode").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains(a, platform);
        Assert.Contains(b, platform);
    }
}
