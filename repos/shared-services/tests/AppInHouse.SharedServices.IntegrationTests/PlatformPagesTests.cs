using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>The pages of the platform services against a stand-in API: what they show, what they post, what they say when the API is down or refuses.</summary>
public class PlatformPagesTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;

    public PlatformPagesTests(WebFactory factory)
    {
        _factory = factory;
        var api = factory.Api;
        api.Failure = null;
        api.Calls.Clear();
        api.Items = [];
        api.Overview = null;
        api.DispatchResponse = null;
        api.NotificationLogs = [];
        api.Subscriptions = [];
        api.TestResult = new WebhookTestResult(true, 200, null, 12);
        api.Queues = [.. new[] { "email", "chat", "media-thumbnail", "outbox", "webhook" }.Select(n => new JobQueueStatusDto(n, "purpose of " + n, 0, 0, 3, n == "webhook" ? 2 : 0, n == "webhook" ? "Degraded" : "Healthy"))];
        api.Projects =
        [
            new("default", "Platform Core", "The platform", "Production", null, null, null, true, DateTimeOffset.UtcNow),
            new("crm", "CRM", "Customers", "Staging", 100, 50, 1024L * 1024 * 1024, true, DateTimeOffset.UtcNow),
        ];
    }

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""");
        Assert.True(match.Success, "no anti-forgery token on the page");
        return match.Groups[1].Value;
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    /// <summary>Opens the page, takes its anti-forgery token and posts the fields to a handler, as a browser would.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string handler, params (string Name, string Value)[] fields)
    {
        var token = TokenIn(await browser.GetStringAsync(page));
        var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))]);
        return await browser.PostAsync($"{page}?handler={handler}", content);
    }

    // ------------------------------------------------------------------------------------------------ notifications

    [Fact]
    public async Task TheNotificationPage_ListsTheTemplatesWithTheirPlaceholders_AndTheRecentDispatches()
    {
        _factory.Api.NotificationLogs =
        [
            new(Guid.NewGuid(), "crm", "UserRegistered", "Email", "Sent", "lan@example.com", 120, DateTimeOffset.UtcNow.AddSeconds(-3)),
            new(Guid.NewGuid(), "crm", "SecurityAlert", "Email→Chat (fallback)", "Fallback", "ops", 340, DateTimeOffset.UtcNow.AddSeconds(-9)),
            new(Guid.NewGuid(), "crm", "PasswordReset", "Email", "Failed", "x@example.com", 80, DateTimeOffset.UtcNow.AddSeconds(-20)),
        ];

        var response = await Browser().GetAsync("/Notifications/Index");
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-template=\"UserRegistered\"", html);
        Assert.Contains("data-template=\"SecurityAlert\"", html);
        Assert.Contains("<code>Details</code>", html);                       // the placeholder the caller must fill
        Assert.Matches("""id="metric-templates">2<""", html);
        Assert.Matches("""id="metric-success">66[.,]7%<""", html);      // 2 of 3 delivered something
        Assert.Matches("""id="metric-today">3<""", html);
        Assert.Contains("Qua kênh dự phòng", html);
        Assert.Contains("Thất bại", html);
        Assert.Contains("lan@example.com", html);
        Assert.DoesNotContain("id=\"load-error\"", html);
    }

    [Fact]
    public async Task WithNoDispatchesYet_ThePageSaysSo_AndTheRateIsADash()
    {
        var html = await BodyAsync(await Browser().GetAsync("/Notifications/Index"));

        Assert.Contains("id=\"no-dispatches\"", html);
        Assert.Contains("id=\"metric-success\">—<", html);
    }

    [Fact]
    public async Task WhenTheApiIsDown_ThePageStillOpens_AndExplains()
    {
        _factory.Api.Failure = new HttpRequestException("connection refused");

        var response = await Browser().GetAsync("/Notifications/Index");
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"load-error\"", html);
        Assert.Contains("Không kết nối được Shared Services API", html);
    }

    [Fact]
    public async Task SendingANotification_PostsTheTemplateTheRecipientsAndTheParsedValues_ThenShowsTheResult()
    {
        var browser = Browser();

        var response = await PostAsync(browser, "/Notifications/Index", "Dispatch",
            ("Form.ProjectCode", "  CRM "), ("Form.TemplateCode", "UserRegistered"), ("Form.RecipientEmail", " lan@example.com "), ("Form.RecipientChatId", ""),
            ("Form.DataText", "UserName=Lan\r\nActionUrl=https://app.example.com/v?a=1&b=2\r\n  AppName = My App \r\nnot a pair\r\n"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var sent = Assert.IsType<DispatchNotificationRequest>(_factory.Api.LastDispatch);
        Assert.Equal(("UserRegistered", "crm", "lan@example.com", (string?)null, (bool?)null), (sent.TemplateCode, sent.ProjectCode, sent.RecipientEmail, sent.RecipientChatId, sent.Queue));
        Assert.Equal(new Dictionary<string, string> { ["UserName"] = "Lan", ["ActionUrl"] = "https://app.example.com/v?a=1&b=2", ["AppName"] = "My App" }, sent.Data);
        var after = await BodyAsync(await browser.GetAsync(response.Headers.Location!.ToString()));
        Assert.Contains("id=\"flash-message\"", after);
        Assert.Contains("Đã gửi thông báo UserRegistered", after);
    }

    [Fact]
    public async Task TheQueueBox_QueuesInsteadOfSending_AndTheMessageSaysSo()
    {
        var browser = Browser();

        var response = await PostAsync(browser, "/Notifications/Index", "Dispatch", ("Form.TemplateCode", "UserRegistered"), ("Form.RecipientEmail", "lan@example.com"), ("Form.Queue", "true"));

        Assert.True(_factory.Api.LastDispatch!.Queue);
        Assert.Contains("hàng đợi", await BodyAsync(await browser.GetAsync(response.Headers.Location!.ToString())));
    }

    [Fact]
    public async Task AFallback_AndAPartialDelivery_AreSaidInPlainWords()
    {
        var browser = Browser();
        _factory.Api.DispatchResponse = new DispatchNotificationResponse(true, "Fallback", [new("Email", false, "smtp down", null, false), new("Chat", true, null, Guid.NewGuid(), true)], 90, Guid.NewGuid(), []);
        var fallback = await PostAsync(browser, "/Notifications/Index", "Dispatch", ("Form.TemplateCode", "SecurityAlert"), ("Form.RecipientEmail", "lan@example.com"));
        Assert.Contains("kênh dự phòng", await BodyAsync(await browser.GetAsync(fallback.Headers.Location!.ToString())));

        _factory.Api.DispatchResponse = new DispatchNotificationResponse(true, "Partial", [new("Email", true, null, Guid.NewGuid(), false), new("Chat", false, "bot blocked", null, false)], 90, Guid.NewGuid(), []);
        var partial = await PostAsync(browser, "/Notifications/Index", "Dispatch", ("Form.TemplateCode", "SecurityAlert"), ("Form.RecipientEmail", "lan@example.com"));
        var html = await BodyAsync(await browser.GetAsync(partial.Headers.Location!.ToString()));
        Assert.Contains("Chỉ một trong hai kênh", html);
        Assert.Contains("Chat: bot blocked", html);
    }

    [Fact]
    public async Task WhenTheApiRefusesTheNotification_TheFormStaysFilledAndShowsTheReason()
    {
        _factory.Api.Failure = null;
        var browser = Browser();
        var token = TokenIn(await browser.GetStringAsync("/Notifications/Index"));
        _factory.Api.Failure = new SharedServicesApiException(400, "One or more values are not valid.", new Dictionary<string, string[]> { ["Data"] = ["The template 'UserRegistered' needs a value for: ActionUrl."] });

        var response = await browser.PostAsync("/Notifications/Index?handler=Dispatch", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", token), new("Form.TemplateCode", "UserRegistered"), new("Form.RecipientEmail", "lan@example.com"), new("Form.DataText", "UserName=Lan")]));
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"form-error\"", html);
        Assert.Contains("needs a value for: ActionUrl", html);
        Assert.Contains("value=\"lan@example.com\"", html);
        Assert.Contains("UserName=Lan", html);
    }

    [Fact]
    public async Task WithoutATemplate_NothingIsSent()
    {
        var response = await PostAsync(Browser(), "/Notifications/Index", "Dispatch", ("Form.RecipientEmail", "lan@example.com"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hãy chọn mẫu thông báo", await BodyAsync(response));
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("dispatch:"));
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryToken_IsRefused()
    {
        var response = await Browser().PostAsync("/Notifications/Index?handler=Dispatch", new FormUrlEncodedContent([new("Form.TemplateCode", "UserRegistered")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("dispatch:"));
    }

    // ------------------------------------------------------------------------------------------------ jobs

    [Fact]
    public async Task TheJobPage_ShowsTheFiveQueues_ARetryButtonOnlyWhereJobsFailed_AndTheSchedules()
    {
        var response = await Browser().GetAsync("/Jobs/Index");
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var queue in new[] { "email", "chat", "media-thumbnail", "outbox", "webhook" }) Assert.Contains($"data-queue=\"{queue}\"", html);
        Assert.Single(Regex.Matches(html, "retry-button"));
        Assert.Matches("""(?s)data-queue="webhook".*?retry-button""", html);
        Assert.Matches("""id="metric-failed">2<""", html);
        Assert.Matches("""id="metric-completed">15<""", html);
        Assert.Contains("data-task=\"log-retention\"", html);
        Assert.Contains("0 2 * * *", html);
        Assert.Contains("Thử lại 1 s · 5 s · 15 s", html);
    }

    [Fact]
    public async Task RetryingAQueue_CallsTheApi_AndSaysHowManyWereRequeued()
    {
        var browser = Browser();

        var response = await PostAsync(browser, "/Jobs/Index", "Retry&queue=webhook");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("retry-failed:webhook", _factory.Api.Calls);
        Assert.Contains("Đã đưa 2 tác vụ lỗi của hàng đợi webhook", await BodyAsync(await browser.GetAsync(response.Headers.Location!.ToString())));
    }

    [Fact]
    public async Task RunningATask_CallsTheApi_AndShowsItsResult_AnUnknownOneShowsTheError()
    {
        var browser = Browser();

        var ok = await PostAsync(browser, "/Jobs/Index", "Trigger&id=log-retention");
        Assert.Contains("run-task:log-retention", _factory.Api.Calls);
        Assert.Contains("Success: 0 old log rows removed.", await BodyAsync(await browser.GetAsync(ok.Headers.Location!.ToString())));

        var unknown = await PostAsync(browser, "/Jobs/Index", "Trigger&id=nope");
        var html = await BodyAsync(await browser.GetAsync(unknown.Headers.Location!.ToString()));
        Assert.Contains("id=\"flash-error\"", html);
        Assert.Contains("There is no scheduled task 'nope'", html);
    }

    [Fact]
    public async Task TheJobPage_WhenTheApiRefusesTheKey_SaysTheKeyIsNotAllowed()
    {
        _factory.Api.Failure = new SharedServicesApiException(403, "forbidden");

        var html = await BodyAsync(await Browser().GetAsync("/Jobs/Index"));

        Assert.Contains("id=\"load-error\"", html);
        Assert.Contains("không đủ quyền", html);
        Assert.Contains("id=\"no-queues\"", html);
    }

    // ------------------------------------------------------------------------------------------------ webhooks

    [Fact]
    public async Task TheWebhookPage_ListsTheInboundSources_AndTheSubscriptionsWithAMaskedKey()
    {
        _factory.Api.InboundSources =
        [
            new("telegram", "/api/v1/webhooks/incoming/telegram", "Secret token", "Telegram updates", true, 4, 1, DateTimeOffset.UtcNow),
            new("github", "/api/v1/webhooks/incoming/github", "HMAC", "GitHub events", false, 0, 0, null),
        ];
        _factory.Api.Subscriptions =
        [
            new(Guid.NewGuid(), "crm", "CRM app", "https://crm.example.com/hook", "••••••••AB12", ["email.sent", "file.uploaded"], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-2), "200", 9, 1, 90),
        ];

        var html = await BodyAsync(await Browser().GetAsync("/Webhooks/Index"));

        Assert.Contains("data-source=\"telegram\"", html);
        Assert.Contains("Đã cấu hình bí mật", html);
        Assert.Contains("Chưa có bí mật", html);          // GitHub has none: the page says every call is refused
        Assert.Contains("https://crm.example.com/hook", html);
        Assert.Contains("••••••••AB12", html);
        Assert.Contains("90%", html);
        Assert.Contains("9 / 10", html);
        Assert.Contains("email.sent", html);
    }

    [Fact]
    public async Task ANewSubscription_ShowsItsKeyOnce_InTheAnswer_NeverInACookieOrOnTheNextVisit()
    {
        var browser = Browser();
        var token = TokenIn(await browser.GetStringAsync("/Webhooks/Index"));

        var response = await browser.PostAsync("/Webhooks/Index?handler=Create", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token), new("Form.ProjectCode", "crm"), new("Form.Name", "CRM app"), new("Form.TargetUrl", "https://crm.example.com/hook"),
            new("Form.Events", "email.sent"), new("Form.Events", "file.uploaded"),
        ]));
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // the answer itself, not a redirect that would have to carry the key
        Assert.Contains($"id=\"new-secret\" value=\"{FakeSharedServicesClient.IssuedWebhookSecret}\"", html);
        Assert.Contains("chỉ hiển thị một lần", html);
        var request = Assert.IsType<CreateWebhookSubscriptionRequest>(_factory.Api.LastSubscription);
        Assert.Equal(("CRM app", "https://crm.example.com/hook", "crm", (string?)null), (request.Name, request.TargetUrl, request.ProjectCode, request.SecretKey));
        Assert.Equal(["email.sent", "file.uploaded"], request.Events);

        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.Contains(FakeSharedServicesClient.IssuedWebhookSecret));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
        var reloaded = await BodyAsync(await browser.GetAsync("/Webhooks/Index"));
        Assert.DoesNotContain(FakeSharedServicesClient.IssuedWebhookSecret, reloaded);
        Assert.DoesNotContain("new-secret-box", reloaded);
    }

    [Fact]
    public async Task AnOwnKeyTypedInTheForm_IsPassedOn()
    {
        await PostAsync(Browser(), "/Webhooks/Index", "Create", ("Form.Name", "App"), ("Form.TargetUrl", "https://a.example.com/h"), ("Form.Events", "*"), ("Form.SecretKey", " my-own-signing-key-0123 "));

        Assert.Equal("my-own-signing-key-0123", _factory.Api.LastSubscription!.SecretKey);
    }

    [Fact]
    public async Task ARefusedSubscription_ShowsTheReason_AndNoKey()
    {
        var browser = Browser();
        var token = TokenIn(await browser.GetStringAsync("/Webhooks/Index"));
        _factory.Api.Failure = new SharedServicesApiException(400, "invalid", new Dictionary<string, string[]> { ["TargetUrl"] = ["10.0.0.5 is on a private or loopback network, which this service is set not to contact."] });

        var html = await BodyAsync(await browser.PostAsync("/Webhooks/Index?handler=Create", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", token), new("Form.Name", "App"), new("Form.TargetUrl", "http://10.0.0.5/h"), new("Form.Events", "*")])));

        Assert.Contains("id=\"form-error\"", html);
        Assert.Contains("private or loopback network", html);
        Assert.DoesNotContain("new-secret-box", html);
    }

    [Fact]
    public async Task DeletingAndTestingASubscription_CallTheApi_AndSayWhatHappened()
    {
        var browser = Browser();
        var id = Guid.NewGuid();

        var test = await PostAsync(browser, "/Webhooks/Index", $"Test&id={id}");
        Assert.Contains($"test-subscription:{id}", _factory.Api.Calls);
        Assert.Contains("đã trả lời 200", await BodyAsync(await browser.GetAsync(test.Headers.Location!.ToString())));

        _factory.Api.TestResult = new WebhookTestResult(false, 503, "The application answered 503.", 30);
        var failed = await PostAsync(browser, "/Webhooks/Index", $"Test&id={id}");
        var html = await BodyAsync(await browser.GetAsync(failed.Headers.Location!.ToString()));
        Assert.Contains("id=\"flash-error\"", html);
        Assert.Contains("The application answered 503.", html);

        var deleted = await PostAsync(browser, "/Webhooks/Index", $"Delete&id={id}");
        Assert.Contains($"delete-subscription:{id}", _factory.Api.Calls);
        Assert.Contains("Đã xóa đăng ký webhook", await BodyAsync(await browser.GetAsync(deleted.Headers.Location!.ToString())));
    }

    // ------------------------------------------------------------------------------------------------ projects

    [Fact]
    public async Task TheProjectPage_ShowsEachProjectsUsageAgainstItsQuotas_AndWarnsWhenItIsNearlyUsedUp()
    {
        _factory.Api.Projects[1] = _factory.Api.Projects[1] with { DailyEmailQuota = 10 };   // the fake says 10 emails were sent today: 100%

        var html = await BodyAsync(await Browser().GetAsync("/Projects/Index"));

        Assert.Contains("data-project=\"default\"", html);
        Assert.Contains("data-project=\"crm\"", html);
        Assert.Contains("Không giới hạn", html);      // the platform project
        Assert.Matches("""(?s)data-project="crm".*?progress-bar bg-danger""", html);
        Assert.Matches("""id="metric-projects">2<""", html);
        Assert.Matches("1[.,]0 GB", html);
    }

    [Fact]
    public async Task ANewProject_PostsItsQuotas_TheStorageQuotaInGigabytesBecomingBytes_AndAnEmptyQuotaMeansNoLimit()
    {
        var browser = Browser();

        var response = await PostAsync(browser, "/Projects/Index", "Create",
            ("Form.ProjectCode", " Billing "), ("Form.Name", "Billing"), ("Form.Description", "Invoices"), ("Form.Environment", "Production"),
            ("Form.DailyEmailQuota", "250"), ("Form.DailyChatQuota", ""), ("Form.StorageQuotaGb", "2.5"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var created = Assert.IsType<CreateProjectRequest>(_factory.Api.LastCreatedProject);
        Assert.Equal(("billing", "Billing", "Production", 250, (int?)null, 2684354560L), (created.ProjectCode, created.Name, created.Environment, created.DailyEmailQuota, created.DailyChatQuota, created.StorageQuotaBytes));
        Assert.Contains("Đã tạo dự án billing", await BodyAsync(await browser.GetAsync(response.Headers.Location!.ToString())));
    }

    [Fact]
    public async Task EditingAProject_PostsEveryField_AndTheActiveBoxBecomesTheFlag()
    {
        var browser = Browser();

        await PostAsync(browser, "/Projects/Index", "Update", ("Form.ProjectCode", "crm"), ("Form.Name", "CRM 2"), ("Form.Description", "d"), ("Form.Environment", "Production"),
            ("Form.DailyEmailQuota", "5"), ("Form.DailyChatQuota", "6"), ("Form.StorageQuotaGb", "0.5"), ("Form.IsActive", "true"), ("Form.IsActive", "false"));
        var activated = _factory.Api.LastUpdatedProject!.Value;
        await PostAsync(browser, "/Projects/Index", "Update", ("Form.ProjectCode", "crm"), ("Form.Name", "CRM 2"), ("Form.IsActive", "false"));
        var deactivated = _factory.Api.LastUpdatedProject!.Value;

        Assert.Equal(("crm", "CRM 2", "Production", 5, 6, 536870912L, true), (activated.Code, activated.Request.Name, activated.Request.Environment, activated.Request.DailyEmailQuota, activated.Request.DailyChatQuota, activated.Request.StorageQuotaBytes, activated.Request.IsActive));
        Assert.False(deactivated.Request.IsActive);
    }

    [Fact]
    public async Task ThePlatformProject_CannotBeSwitchedOffFromThePage()
    {
        var html = await BodyAsync(await Browser().GetAsync("/Projects/Index"));

        var platformEditor = Regex.Match(html, """(?s)id="edit-default".*?</form>""").Value;
        Assert.Contains("disabled", platformEditor);
        Assert.Contains("name=\"Form.IsActive\" value=\"true\"", platformEditor.Replace("type=\"hidden\" ", string.Empty));
        Assert.DoesNotContain("name=\"Form.IsActive\" value=\"false\"", platformEditor);
    }

    [Fact]
    public async Task ABadProjectForm_ShowsWhatIsWrong_AndTheApiIsNotCalled()
    {
        var response = await PostAsync(Browser(), "/Projects/Index", "Create", ("Form.ProjectCode", "billing"), ("Form.Name", ""), ("Form.DailyEmailQuota", "-5"));
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"form-error\"", html);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("create-project:"));
    }

    [Fact]
    public async Task AProjectTheApiRefuses_ShowsItsReason()
    {
        var browser = Browser();
        var token = TokenIn(await browser.GetStringAsync("/Projects/Index"));
        _factory.Api.Failure = new SharedServicesApiException(409, "Project 'crm' already exists.");

        var html = await BodyAsync(await browser.PostAsync("/Projects/Index?handler=Create", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("Form.ProjectCode", "crm"), new("Form.Name", "CRM")])));

        Assert.Contains("Project 'crm' already exists.", html);
        Assert.Contains("value=\"crm\"", html);
    }

    // ------------------------------------------------------------------------------------------------ the launchpad

    [Fact]
    public async Task TheLaunchpad_ShowsTheHealthOfEachServiceAndTheFiguresOfTheApi()
    {
        var response = await Browser().GetAsync("/");
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Tất cả hệ thống hoạt động ổn định", html);
        Assert.Contains("data-status=\"Healthy\"", html);
        Assert.Matches("""id="metric-services">8 / 8<""", html);
        Assert.Matches("""id="metric-success">95([.,]0)?%<""", html);
        Assert.Contains("120", html);          // the requests of the last 24 hours
        Assert.Matches("2[.,]0 KB", html);       // the storage used
        foreach (var service in new[] { "email", "chat", "storage", "projects", "notifications", "jobs", "webhooks", "monitoring" }) Assert.Contains($"data-service=\"{service}\"", html);
    }

    [Fact]
    public async Task ADegradedService_TurnsItsCardYellow_AndTheHeadlineSaysSo()
    {
        _factory.Api.Overview = FakeSharedServicesClient.HealthyOverview(degraded: "email");

        var html = await BodyAsync(await Browser().GetAsync("/"));

        Assert.Contains("Có dịch vụ đang suy giảm", html);
        Assert.Matches("""(?s)data-service="email" data-status="Warning" """, html);
        Assert.Contains("Suy giảm", html);
        Assert.Matches("""id="metric-services">8 / 8<""", html);   // a degraded service is still running
        Assert.DoesNotContain("Tất cả hệ thống hoạt động ổn định", html);
    }

    [Fact]
    public async Task AnIdleService_IsShownAsNotConfigured_AndWithNoDeliveriesTheRateIsADash()
    {
        var healthy = FakeSharedServicesClient.HealthyOverview();
        _factory.Api.Overview = healthy with
        {
            ServicesHealthy = 6, ServicesIdle = 2, SuccessRatePercent = null,
            Services = [.. healthy.Services.Select(s => s.Id is "chat" or "storage" ? s with { Status = "Idle" } : s)],
        };

        var html = await BodyAsync(await Browser().GetAsync("/"));

        Assert.Matches("""data-service="chat" data-status="Idle" """, html);
        Assert.Contains("Chưa cấu hình", html);
        Assert.Contains("id=\"metric-success\">—<", html);
        Assert.Matches("""id="metric-services">6 / 8<""", html);
    }

    [Fact]
    public async Task WhenTheMetricsCannotBeLoaded_TheLaunchpadStillOpens_WithAWarningAndNoInventedHealth()
    {
        _factory.Api.Failure = new HttpRequestException("connection refused");

        var response = await Browser().GetAsync("/");
        var html = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Không kết nối được Shared Services API", html);
        Assert.Contains("Chưa lấy được trạng thái hệ thống", html);
        Assert.DoesNotContain("data-status=\"Healthy\"", html);
        Assert.Matches("""id="metric-services">0 / 8<""", html);
    }
}

/// <summary>The same pages against the real API: what the service really says, and what the forms really do.</summary>
public class PlatformPagesEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string TokenIn(string html) => Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""").Groups[1].Value;

    private async Task<(HttpClient Browser, HttpClient Admin)> PortalAsync()
    {
        var admin = await PlatformApi.AdminAsync(factory);
        var browser = new PortalOnRealApiFactory(admin).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        return (browser, admin);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string handler, params (string Name, string Value)[] fields)
    {
        var token = TokenIn(await browser.GetStringAsync(page));
        return await browser.PostAsync($"{page}?handler={handler}", new FormUrlEncodedContent([new KeyValuePair<string, string>("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))]));
    }

    [Fact]
    public async Task EveryPage_OpensWithWhatTheServiceReallyHas()
    {
        var (browser, _) = await PortalAsync();

        var notifications = await browser.GetStringAsync("/Notifications/Index");
        var jobs = await browser.GetStringAsync("/Jobs/Index");
        var webhooks = await browser.GetStringAsync("/Webhooks/Index");
        var projects = await browser.GetStringAsync("/Projects/Index");
        var home = await browser.GetStringAsync("/");

        foreach (var code in new[] { "UserRegistered", "PasswordReset", "SecurityAlert", "SystemMaintenance", "PaymentSuccess" }) Assert.Contains($"data-template=\"{code}\"", notifications);
        foreach (var queue in new[] { "email", "chat", "media-thumbnail", "outbox", "webhook" }) Assert.Contains($"data-queue=\"{queue}\"", jobs);
        foreach (var task in new[] { "log-retention", "job-housekeeping", "metrics-aggregate" }) Assert.Contains($"data-task=\"{task}\"", jobs);
        foreach (var source in new[] { "telegram", "github", "stripe" }) Assert.Contains($"data-source=\"{source}\"", webhooks);
        foreach (var project in new[] { "default", "identity", "linguabridge", "crm" }) Assert.Contains($"data-project=\"{project}\"", projects);
        Assert.Contains("data-service=\"jobs\"", home);
        Assert.DoesNotContain("id=\"load-error\"", notifications + jobs + webhooks + projects);
    }

    [Fact]
    public async Task ANotificationSentFromThePage_ReallyGoesOut_AndShowsInTheHistory()
    {
        var (browser, admin) = await PortalAsync();
        var project = PlatformApi.NewProject();
        var to = PlatformApi.NewRecipient();
        await PlatformApi.ActivateMockEmailAsync(admin, project);

        var response = await PostAsync(browser, "/Notifications/Index", "Dispatch", ("Form.ProjectCode", project), ("Form.TemplateCode", "PasswordReset"), ("Form.RecipientEmail", to),
            ("Form.DataText", "UserName=Lan\nAppName=App\nActionUrl=https://app.example.com/reset\nExpiryMinutes=15"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(factory.MockMail.SentMessages, m => m.To == to && m.Subject == "Đặt lại mật khẩu App");
        var page = WebUtility.HtmlDecode(await browser.GetStringAsync(response.Headers.Location!.ToString()));
        Assert.Contains("Đã gửi thông báo PasswordReset", page);
        Assert.Contains(to, page);   // and the history shows it
    }

    [Fact]
    public async Task AMissingValue_IsExplainedOnThePage_ByTheServicesOwnMessage()
    {
        var (browser, admin) = await PortalAsync();
        var project = PlatformApi.NewProject();
        await PlatformApi.ActivateMockEmailAsync(admin, project);

        var response = await PostAsync(browser, "/Notifications/Index", "Dispatch", ("Form.ProjectCode", project), ("Form.TemplateCode", "PasswordReset"), ("Form.RecipientEmail", PlatformApi.NewRecipient()), ("Form.DataText", "UserName=Lan"));
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("needs a value for: AppName, ActionUrl, ExpiryMinutes", html);
    }

    [Fact]
    public async Task AProjectCreatedAndRaisedFromThePage_IsReallyStoredWithItsQuotas()
    {
        var (browser, admin) = await PortalAsync();
        var code = PlatformApi.NewProject();

        await PostAsync(browser, "/Projects/Index", "Create", ("Form.ProjectCode", code), ("Form.Name", "From the portal"), ("Form.Environment", "Production"), ("Form.DailyEmailQuota", "3"), ("Form.StorageQuotaGb", "1"));
        var created = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/projects/{code}/usage");
        Assert.Equal((3, 1073741824L), (created.GetProperty("dailyEmailQuota").GetInt32(), created.GetProperty("storageQuotaBytes").GetInt64()));

        await PostAsync(browser, "/Projects/Index", "Update", ("Form.ProjectCode", code), ("Form.Name", "From the portal"), ("Form.Environment", "Production"), ("Form.DailyEmailQuota", "9"), ("Form.IsActive", "false"));
        var row = await factory.WithDbAsync(db => db.ProjectTenants.AsNoTracking().SingleAsync(p => p.ProjectCode == code));
        Assert.Equal((9, (long?)null, false), (row.DailyEmailQuota, row.StorageQuotaBytes, row.IsActive));
        Assert.Contains($"data-project=\"{code}\"", await browser.GetStringAsync("/Projects/Index"));
    }

    [Fact]
    public async Task ASubscriptionCreatedOnThePage_IsListedWithAMask_CanBeTested_AndDeleted()
    {
        var (browser, admin) = await PortalAsync();
        var project = PlatformApi.NewProject();
        var target = $"https://hooks.example.test/{Guid.NewGuid():N}";

        var created = await PostAsync(browser, "/Webhooks/Index", "Create", ("Form.ProjectCode", project), ("Form.Name", "Portal app"), ("Form.TargetUrl", target), ("Form.Events", "email.sent"));
        var html = WebUtility.HtmlDecode(await created.Content.ReadAsStringAsync());
        var secret = Regex.Match(html, "id=\"new-secret\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.StartsWith("whsec_", secret);

        var listed = await browser.GetStringAsync("/Webhooks/Index");
        Assert.Contains(target, listed);
        Assert.DoesNotContain(secret, listed);
        Assert.Contains("••••••••" + secret[^4..], WebUtility.HtmlDecode(listed));

        var id = (await factory.WithDbAsync(db => db.WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.ProjectCode == project))).Id;
        var tested = await PostAsync(browser, "/Webhooks/Index", $"Test&id={id}");
        Assert.Contains("đã trả lời 200", WebUtility.HtmlDecode(await browser.GetStringAsync(tested.Headers.Location!.ToString())));
        Assert.Contains(factory.Webhook.Calls.ToArray(), c => c.Request.RequestUri!.ToString() == target && c.Request.Headers.Contains("X-AIH-Signature"));

        await PostAsync(browser, "/Webhooks/Index", $"Delete&id={id}");
        Assert.False(await factory.WithDbAsync(db => db.WebhookSubscriptions.AnyAsync(s => s.Id == id)));
        _ = admin;
    }

    [Fact]
    public async Task ARunOfAScheduledTask_FromThePage_IsRecordedAndShown()
    {
        var (browser, _) = await PortalAsync();

        var response = await PostAsync(browser, "/Jobs/Index", "Trigger&id=metrics-aggregate");
        var flash = WebUtility.HtmlDecode(await browser.GetStringAsync(response.Headers.Location!.ToString()));
        var page = WebUtility.HtmlDecode(await browser.GetStringAsync("/Jobs/Index"));

        Assert.Contains("Success:", flash);
        Assert.Matches("""(?s)data-task="metrics-aggregate".*?Success: \d of 8 services healthy""", page);
    }
}
