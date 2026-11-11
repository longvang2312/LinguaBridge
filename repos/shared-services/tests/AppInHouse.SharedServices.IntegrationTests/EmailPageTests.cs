using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppInHouse.SharedServices.IntegrationTests;

public class EmailPageTests : IClassFixture<WebFactory>
{
    private const string Mask = "••••••••";
    private readonly WebFactory _factory;

    public EmailPageTests(WebFactory factory)
    {
        _factory = factory;
        _factory.Api.Failure = null;
        _factory.Api.Calls.Clear();
        _factory.Api.TestResponse = null;
        _factory.Api.Items =
        [
            Smtp("Default email", 2, "Active", "default"),
            Smtp("Default email", 1, "Archived", "default"),
            WebFactory.Config("Default email", 1, "Active", project: "linguabridge", channel: "Email", provider: "SendGrid") with
            {
                Settings = Json("""{"senderEmail":"lb@example.com","senderName":"LinguaBridge"}"""),
            },
            WebFactory.Config("Default email", 1, "Active", project: "identity", channel: "Email", provider: "Mock", hasSecret: false) with
            {
                Settings = Json("""{"senderEmail":"id@example.com"}"""),
            },
        ];
        _factory.Api.Logs =
        [
            new(Guid.NewGuid(), "default", "Email", "ok@example.com", "Sent", "SMTP server accepted the message", 42, new DateTimeOffset(2026, 11, 11, 8, 30, 5, TimeSpan.Zero)),
            new(Guid.NewGuid(), "default", "Email", "bad@example.com", "Failed", "The SMTP server rejected the login.", null, new DateTimeOffset(2026, 11, 11, 8, 20, 0, TimeSpan.Zero)),
        ];
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static ChannelConfigurationResponse Smtp(string name, int version, string status, string project) =>
        WebFactory.Config(name, version, status, project: project, channel: "Email", provider: "Smtp") with
        {
            Settings = Json("""{"host":"smtp.example.com","port":587,"useSsl":"StartTls","username":"mailer","senderEmail":"noreply@example.com","senderName":"AppInHouse"}"""),
        };

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "no anti-forgery token on the page");
        return match.Groups[1].Value;
    }

    /// <summary>What the browser would post for the form, with a few fields changed.</summary>
    private static List<KeyValuePair<string, string>> FormFields(string token, Dictionary<string, string>? changes = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Form.ProjectCode"] = "default",
            ["Form.Name"] = "Default email",
            ["Form.ProviderType"] = "Smtp",
            ["Form.SmtpHost"] = "smtp.example.com",
            ["Form.SmtpPort"] = "587",
            ["Form.SmtpSecurity"] = "StartTls",
            ["Form.SmtpUsername"] = "mailer",
            ["Form.SenderEmail"] = "noreply@example.com",
            ["Form.SenderName"] = "AppInHouse",
            ["Form.Secret"] = Mask,
            ["Form.TestRecipient"] = "me@example.com",
        };
        foreach (var (key, value) in changes ?? []) fields[key] = value;
        return [.. fields];
    }

    private async Task<(HttpClient Client, string Token, string Html)> OpenAsync(string url = "/Email")
    {
        var client = NewClient();
        var html = await client.GetStringAsync(url);
        return (client, TokenIn(html), html);
    }

    private static async Task<JsonElement> PostTestAsync(HttpClient client, string token, Dictionary<string, string>? changes = null, bool withHeader = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Email?handler=SendTest") { Content = new FormUrlEncodedContent(FormFields(token, changes)) };
        if (withHeader) request.Headers.Add("RequestVerificationToken", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ------------------------------------------------------------------------------------------------ the form

    [Fact]
    public async Task ThePage_HasTheFormForSmtp_SendGridAndMock()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Contains("Cấu hình Email", html);
        Assert.Contains("bootstrap@5.3.3", html);
        foreach (var provider in new[] { "Smtp", "SendGrid", "Mock" })
            Assert.Contains($"<option value=\"{provider}\"", html);

        // SMTP: host, port, SSL, sender email, sender name, password.
        foreach (var id in new[] { "Form_SmtpHost", "Form_SmtpPort", "Form_SmtpSecurity", "Form_SenderEmail", "Form_SenderName", "Form_Secret" })
            Assert.Contains($"id=\"{id}\"", html);
        foreach (var option in new[] { "Auto", "StartTls", "SslOnConnect", "None" })
            Assert.Contains($"<option value=\"{option}\"", html);
        Assert.Contains("type=\"password\"", html);

        // SendGrid and Mock sections exist (one is shown, the script switches), the mock one saying no secret is needed.
        Assert.Contains("data-provider=\"SendGrid\"", html);
        Assert.Contains("data-provider=\"Mock\"", html);
        Assert.Contains("không cần secret", html);

        // Test, save and activate.
        Assert.Contains("id=\"Form_TestRecipient\"", html);
        Assert.Contains("id=\"send-test-button\"", html);
        Assert.Contains("Gửi Thử Nghiệm", html);
        Assert.Contains("id=\"send-test-spinner\"", html);
        Assert.Contains("id=\"send-test-result\"", html);
        Assert.Contains("Lưu Bản Nháp", html);
        Assert.Contains("Lưu &amp; Kích Hoạt", html);
    }

    [Fact]
    public async Task TheFormIsFilledFromTheActiveConfiguration_AndTheSecretIsOnlyTheMask()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Contains("value=\"smtp.example.com\"", html);
        Assert.Contains("value=\"587\"", html);
        Assert.Contains("value=\"mailer\"", html);
        Assert.Contains("value=\"noreply@example.com\"", html);
        Assert.Contains("<option value=\"StartTls\" selected", html);
        Assert.Contains("<option value=\"Smtp\" selected", html);
        Assert.Contains($"id=\"Form_Secret\" name=\"Form.Secret\" value=\"{Mask}\"", html);
        Assert.Contains("phiên bản <strong>v2</strong>", html);    // the active version, not the archived v1
        Assert.Contains("Active", html);
    }

    [Fact]
    public async Task AnotherProject_LoadsItsOwnConfiguration()
    {
        var (_, _, sendGrid) = await OpenAsync("/Email?project=linguabridge");
        var (_, _, mock) = await OpenAsync("/Email?project=identity");

        Assert.Contains("<option value=\"SendGrid\" selected", sendGrid);
        Assert.Contains("value=\"lb@example.com\"", sendGrid);
        Assert.Contains("API Key", sendGrid);
        Assert.DoesNotContain("value=\"smtp.example.com\"", sendGrid);   // (the host input has it only as a placeholder)
        Assert.Contains("<option value=\"Mock\" selected", mock);
        Assert.Matches(@"id=""secret-group""", mock);
        Assert.Contains("secret-group\"", mock);
        Assert.Contains("d-none\" id=\"secret-group\"", mock);   // no secret for a mock
    }

    [Fact]
    public async Task AProjectWithoutAConfiguration_ShowsAnEmptyFormAndSaysDefaultIsUsed()
    {
        var (_, _, html) = await OpenAsync("/Email?project=shop");

        Assert.Contains("<option value=\"shop\" selected", html);
        Assert.Contains("chưa có cấu hình email", html);
        Assert.DoesNotContain("value=\"smtp.example.com\"", html);
        Assert.DoesNotContain($"value=\"{Mask}\"", html);
    }

    [Fact]
    public async Task TheProjectSelector_OffersTheSuggestedProjects_AndTheOnesThatHaveAConfiguration()
    {
        var (_, _, html) = await OpenAsync();

        foreach (var project in new[] { "default", "linguabridge", "identity" })
            Assert.Contains($"<option value=\"{project}\"", html);
    }

    [Fact]
    public async Task TheSecretOfAStoredConfiguration_NeverReachesThePage()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Single(Regex.Matches(html, @"id=""Form_Secret""[^>]*value=""([^""]*)"""));
        Assert.Equal(Mask, Regex.Match(html, @"id=""Form_Secret""[^>]*value=""([^""]*)""").Groups[1].Value);
    }

    // ------------------------------------------------------------------------------------------------ a newer draft next to the live version

    private void ADraftNewerThanTheActiveVersion()
    {
        _factory.Api.Items =
        [
            Smtp("Default email", 1, "Archived", "default"),
            Smtp("Default email", 2, "Active", "default"),
            Smtp("Default email", 3, "Draft", "default") with { Settings = Json("""{"host":"draft.example.org","port":2525,"useSsl":"None","username":"mailer","senderEmail":"noreply@example.com","senderName":"AppInHouse"}""") },
        ];
    }

    [Fact]
    public async Task WhenADraftIsNewerThanTheActiveVersion_TheFormShowsTheDraft_AndNamesTheLiveVersion()
    {
        ADraftNewerThanTheActiveVersion();

        var (_, _, html) = await OpenAsync();

        Assert.Contains("value=\"draft.example.org\"", html);                 // the draft's values, not the active version's
        Assert.DoesNotContain("value=\"smtp.example.com\"", html);
        Assert.Contains("value=\"2525\"", html);
        Assert.Contains("phiên bản <strong>v3</strong>", html);
        Assert.Contains("text-bg-warning\">Draft", html);
        Assert.Contains("id=\"live-version\"", html);
        Assert.Contains("Đang hoạt động: <strong>v2</strong>", html);
        Assert.Contains($"value=\"{Mask}\"", html);                           // and the mask stands for the DRAFT's secret
    }

    [Fact]
    public async Task WhenTheShownVersionIsTheActiveOne_ThereIsNoLiveVersionNote()
    {
        var (_, _, html) = await OpenAsync();

        Assert.DoesNotContain("id=\"live-version\"", html);
    }

    [Fact]
    public async Task SendTest_WithTheMaskAndTheDraftsSettings_TestsTheDraft_NotTheActiveVersion()
    {
        ADraftNewerThanTheActiveVersion();
        var draft = _factory.Api.Items.Single(i => i.Version == 3);
        var (client, token, _) = await OpenAsync();

        var answer = await PostTestAsync(client, token, new() { ["Form.SmtpHost"] = "draft.example.org", ["Form.SmtpPort"] = "2525", ["Form.SmtpSecurity"] = "None" });

        Assert.True(answer.GetProperty("success").GetBoolean());
        Assert.Equal(draft.Id, _factory.Api.LastTestRequest!.ConfigurationId);   // the form as shown is the draft, so the draft is what is tried
    }

    [Fact]
    public async Task SendTest_WithTheActiveVersionsOldSettings_ButTheMask_IsNotTreatedAsUnchanged()
    {
        // The form shows the draft; settings equal to the OLD active version are a change, and the mask is not a password for them.
        ADraftNewerThanTheActiveVersion();
        var (client, token, _) = await OpenAsync();

        var answer = await PostTestAsync(client, token);   // default fields: smtp.example.com, 587, StartTls

        Assert.False(answer.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-email:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ delivery logs

    [Fact]
    public async Task TheRecentDeliveries_AreListedWithBadgesRecipientLatencyAndTime()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Contains("Lịch sử gửi email gần đây", html);
        Assert.Contains("id=\"delivery-logs\"", html);
        Assert.Contains("ok@example.com", html);
        Assert.Contains("bad@example.com", html);
        Assert.Contains("text-bg-success\">Thành công", html);
        Assert.Contains("text-bg-danger\">Thất bại", html);
        Assert.Contains("42 ms", html);
        Assert.Contains("2026-11-11 08:30:05", html);
        Assert.Contains("The SMTP server rejected the login.", html);
        Assert.Contains(_factory.Api.Calls, c => c == "logs:default:20");
    }

    [Fact]
    public async Task WithoutDeliveries_ThePageSaysSo()
    {
        _factory.Api.Logs = [];

        var (_, _, html) = await OpenAsync();

        Assert.Contains("id=\"no-logs\"", html);
    }

    // ------------------------------------------------------------------------------------------------ CSRF

    [Fact]
    public async Task ThePostForm_CarriesAnAntiForgeryToken_AndTheScriptSendsItWithTheTest()
    {
        var (_, _, html) = await OpenAsync();

        var form = Regex.Match(html, """<form[^>]*id="email-form"[^>]*>(.*?)</form>""", RegexOptions.Singleline).Value;
        Assert.Contains("method=\"post\"", form);
        Assert.Contains("__RequestVerificationToken", form);
        Assert.Contains("'RequestVerificationToken'", html);   // the script puts it in a header
        Assert.Contains("textContent = ''", html);
        Assert.Contains("createTextNode", html);               // results are shown as text, never as HTML
    }

    [Theory]
    [InlineData("/Email?handler=SaveDraft")]
    [InlineData("/Email?handler=SaveActivate")]
    public async Task ASave_WithoutTheToken_IsRefused_AndNothingIsSaved(string url)
    {
        var (client, _, _) = await OpenAsync();

        var response = await client.PostAsync(url, new FormUrlEncodedContent(FormFields(token: string.Empty).Where(f => f.Key != "__RequestVerificationToken")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("save:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASendTest_WithoutTheToken_IsRefused_AndNothingIsSent()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/Email?handler=SendTest", new FormUrlEncodedContent(FormFields(token).Where(f => f.Key != "__RequestVerificationToken")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-email:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATokenFromAnotherBrowser_IsRefused()
    {
        var (_, token, _) = await OpenAsync();
        var stranger = NewClient();   // no anti-forgery cookie

        var response = await stranger.PostAsync("/Email?handler=SendTest", new FormUrlEncodedContent(FormFields(token)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheTokenInTheHeader_IsEnoughForTheScript_WithoutTheFormField()
    {
        var (client, token, _) = await OpenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Email?handler=SendTest")
        {
            Content = new FormUrlEncodedContent(FormFields(token).Where(f => f.Key != "__RequestVerificationToken")),
        };
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ saving

    [Fact]
    public async Task SaveDraft_SendsTheSettingsToTheApi_WithoutASecretWhenTheMaskCameBack_AndDoesNotActivate()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new() { ["Form.SmtpHost"] = "mail.example.org" })));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Email?project=default", response.Headers.Location!.OriginalString);
        var save = _factory.Api.LastSave!;
        Assert.Equal(("default", "Email", "Smtp", "Default email"), (save.ProjectCode, save.ChannelType, save.ProviderType, save.Name));
        Assert.Null(save.Secret);   // the mask means "keep the stored secret"
        Assert.Equal("mail.example.org", save.Settings!.Value.GetProperty("host").GetString());
        Assert.Equal(587, save.Settings.Value.GetProperty("port").GetInt32());
        Assert.Equal("StartTls", save.Settings.Value.GetProperty("useSsl").GetString());
        Assert.Equal("mailer", save.Settings.Value.GetProperty("username").GetString());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("activate:", StringComparison.Ordinal));
        Assert.Contains("Đã lưu bản nháp", await client.GetStringAsync(response.Headers.Location.OriginalString));
    }

    [Fact]
    public async Task SaveAndActivate_SavesThenActivatesTheSavedVersion()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/Email?handler=SaveActivate", new FormUrlEncodedContent(FormFields(token)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var calls = _factory.Api.Calls.Where(c => c.StartsWith("save:", StringComparison.Ordinal) || c.StartsWith("activate:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("save:", calls[0]);
        Assert.StartsWith("activate:", calls[1]);
        Assert.Contains("Đã lưu và kích hoạt", await client.GetStringAsync(response.Headers.Location!.OriginalString));
    }

    [Fact]
    public async Task ATypedSecret_IsSentToTheApi_AndNeverComesBackInThePage()
    {
        const string typed = "Brand-new-secret-123";
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new() { ["Form.Secret"] = typed })));

        Assert.Equal(typed, _factory.Api.LastSave!.Secret);
        Assert.DoesNotContain(typed, await client.GetStringAsync(response.Headers.Location!.OriginalString));
    }

    [Fact]
    public async Task EachProvider_SendsOnlyItsOwnSettings()
    {
        var (client, token, _) = await OpenAsync();

        await client.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new() { ["Form.ProviderType"] = "SendGrid", ["Form.Secret"] = "SG-key-0005" })));
        var sendGrid = _factory.Api.LastSave!;
        await client.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new() { ["Form.ProviderType"] = "Mock", ["Form.Secret"] = string.Empty })));
        var mock = _factory.Api.LastSave!;

        Assert.Equal("SendGrid", sendGrid.ProviderType);
        Assert.False(sendGrid.Settings!.Value.TryGetProperty("host", out _));   // the SMTP fields are not carried along
        Assert.Equal("noreply@example.com", sendGrid.Settings.Value.GetProperty("senderEmail").GetString());
        Assert.Equal("SG-key-0005", sendGrid.Secret);
        Assert.Equal("Mock", mock.ProviderType);
        Assert.False(mock.Settings!.Value.TryGetProperty("host", out _));
        Assert.Null(mock.Secret);
    }

    [Fact]
    public async Task WhenTheApiRefusesASave_TheUserSeesTheReason()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.Failure = new SharedServicesApiException(400, "One or more values are not valid.",
            new Dictionary<string, string[]> { ["Secret"] = ["The provider changed from Smtp to SendGrid: enter the secret for SendGrid."] });

        var response = await client.PostAsync("/Email?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new() { ["Form.ProviderType"] = "SendGrid" })));
        _factory.Api.Failure = null;

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var follow = await client.GetStringAsync(response.Headers.Location!.OriginalString);
        Assert.Contains("id=\"flash-error\"", follow);
        Assert.Contains("enter the secret for SendGrid", follow);
    }

    // ------------------------------------------------------------------------------------------------ Send Test

    [Fact]
    public async Task SendTest_WithATypedSecret_TriesTheValuesOnTheScreen_FromMemory()
    {
        var (client, token, _) = await OpenAsync();

        var answer = await PostTestAsync(client, token, new() { ["Form.Secret"] = "Typed-pass-999", ["Form.SmtpHost"] = "other.example.org" });

        Assert.True(answer.GetProperty("success").GetBoolean());
        var request = _factory.Api.LastTestRequest!;
        Assert.Null(request.ConfigurationId);                                  // not a saved version: nothing is stored
        Assert.Equal(("me@example.com", "Smtp", "Typed-pass-999"), (request.TestRecipient, request.ProviderType, request.Secret));
        Assert.Equal("other.example.org", request.Settings!.Value.GetProperty("host").GetString());
        Assert.Contains(_factory.Api.Calls, c => c == "test-email:unsaved:Smtp");
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("save:", StringComparison.Ordinal) || c.StartsWith("activate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTestAnswer_NeverContainsTheTypedSecret()
    {
        const string typed = "Typed-pass-secret-777";
        var (client, token, _) = await OpenAsync();
        _factory.Api.TestResponse = new SendTestEmailResponse(false, "The SMTP server rejected the login.", 80, "Smtp", false, null);

        var answer = await PostTestAsync(client, token, new() { ["Form.Secret"] = typed });

        Assert.False(answer.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(typed, answer.GetRawText());
        Assert.Contains("rejected the login", answer.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SendTest_WithTheMaskAndTheSavedSettings_TestsTheSavedVersion()
    {
        var (client, token, _) = await OpenAsync();
        var saved = _factory.Api.Items.First(i => i.Version == 2);

        var answer = await PostTestAsync(client, token);   // the form as it was loaded: the mask, nothing changed

        Assert.True(answer.GetProperty("success").GetBoolean());
        Assert.Equal(saved.Id, _factory.Api.LastTestRequest!.ConfigurationId);
        Assert.Null(_factory.Api.LastTestRequest.Secret);
        Assert.Null(_factory.Api.LastTestRequest.Settings);
        Assert.Contains("đã thử cấu hình đã lưu", answer.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SendTest_WithTheMaskButChangedSettings_AsksForTheSecretAgain_AndDoesNotBorrowTheStoredOne()
    {
        var (client, token, _) = await OpenAsync();

        var answer = await PostTestAsync(client, token, new() { ["Form.SmtpHost"] = "elsewhere.example.net" });

        Assert.False(answer.GetProperty("success").GetBoolean());
        Assert.Contains("mật khẩu SMTP", answer.GetProperty("message").GetString());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-email:", StringComparison.Ordinal));   // nothing was tried
    }

    [Fact]
    public async Task SendTest_ForSendGrid_FollowsTheSameRules()
    {
        var (client, token, _) = await OpenAsync("/Email?project=linguabridge");
        var sendGridFields = new Dictionary<string, string>
        {
            ["Form.ProjectCode"] = "linguabridge", ["Form.ProviderType"] = "SendGrid", ["Form.SenderEmail"] = "lb@example.com", ["Form.SenderName"] = "LinguaBridge",
        };

        var unchanged = await PostTestAsync(client, token, sendGridFields);
        Assert.True(unchanged.GetProperty("success").GetBoolean());
        Assert.NotNull(_factory.Api.LastTestRequest!.ConfigurationId);

        _factory.Api.Calls.Clear();
        var changed = await PostTestAsync(client, token, new(sendGridFields) { ["Form.SenderEmail"] = "other@example.com" });
        Assert.False(changed.GetProperty("success").GetBoolean());
        Assert.Contains("API key", changed.GetProperty("message").GetString());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-email:", StringComparison.Ordinal));

        var typed = await PostTestAsync(client, token, new(sendGridFields) { ["Form.SenderEmail"] = "other@example.com", ["Form.Secret"] = "SG-typed-key-0006" });
        Assert.True(typed.GetProperty("success").GetBoolean());
        Assert.Equal("SG-typed-key-0006", _factory.Api.LastTestRequest!.Secret);
        Assert.Null(_factory.Api.LastTestRequest.ConfigurationId);
    }

    [Fact]
    public async Task SendTest_ForSendGrid_WithNothingSavedAndNoKey_SaysToTypeOne()
    {
        var (client, token, _) = await OpenAsync("/Email?project=shop");

        var answer = await PostTestAsync(client, token, new() { ["Form.ProjectCode"] = "shop", ["Form.ProviderType"] = "SendGrid", ["Form.Secret"] = string.Empty });

        Assert.False(answer.GetProperty("success").GetBoolean());
        Assert.Contains("API key", answer.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SendTest_ForMock_NeedsNoSecret()
    {
        var (client, token, _) = await OpenAsync("/Email?project=identity");

        var answer = await PostTestAsync(client, token, new() { ["Form.ProjectCode"] = "identity", ["Form.ProviderType"] = "Mock", ["Form.Secret"] = string.Empty });

        Assert.True(answer.GetProperty("success").GetBoolean());
        Assert.Equal("Mock", _factory.Api.LastTestRequest!.ProviderType);
        Assert.Null(_factory.Api.LastTestRequest.ConfigurationId);
    }

    [Fact]
    public async Task SendTest_WithoutARecipient_SaysSo()
    {
        var (client, token, _) = await OpenAsync();

        var answer = await PostTestAsync(client, token, new() { ["Form.TestRecipient"] = string.Empty });

        Assert.False(answer.GetProperty("success").GetBoolean());
        Assert.Contains("người nhận", answer.GetProperty("message").GetString());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-email:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendTest_WhenTheApiFails_AnswersWithASafeMessage_NotAnErrorPage()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.Failure = new HttpRequestException("connection refused");

        var answer = await PostTestAsync(client, token, new() { ["Form.Secret"] = "Typed-pass-999" });
        _factory.Api.Failure = null;

        Assert.False(answer.GetProperty("success").GetBoolean());
        Assert.Contains("Không kết nối được", answer.GetProperty("message").GetString());
        Assert.DoesNotContain("connection refused", answer.GetRawText());
        Assert.DoesNotContain("Typed-pass-999", answer.GetRawText());
    }

    // ------------------------------------------------------------------------------------------------ the API is not available

    [Fact]
    public async Task WhenTheApiIsDown_ThePageStillOpens_WithTheFormAndAWarning()
    {
        _factory.Api.Failure = new HttpRequestException("connection refused");

        var response = await NewClient().GetAsync("/Email");
        var html = await response.Content.ReadAsStringAsync();
        _factory.Api.Failure = null;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"load-error\"", html);
        Assert.Contains("id=\"email-form\"", html);
        Assert.DoesNotContain("connection refused", html);
    }

    [Fact]
    public async Task TheSidebar_HighlightsEmail_OnThisPage()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Matches(@"class=""nav-link active""\s+href=""/Email""", html);
    }
}
