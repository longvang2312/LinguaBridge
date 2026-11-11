using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.IntegrationTests;

public class ChatPageTests : IClassFixture<WebFactory>
{
    private const string Mask = "••••••••";
    private readonly WebFactory _factory;

    public ChatPageTests(WebFactory factory)
    {
        _factory = factory;
        _factory.Api.Failure = null;
        _factory.Api.Calls.Clear();
        _factory.Api.ChatTestResponse = null;
        _factory.Api.Items =
        [
            Chat("Default chat", 2, "Active", "default", "TelegramBot", """{"defaultChatId":"-1001","parseMode":"PlainText"}"""),
            Chat("Default chat", 1, "Archived", "default", "TelegramBot", """{"defaultChatId":"-999","parseMode":"PlainText"}"""),
            Chat("Default chat", 1, "Active", "linguabridge", "DiscordWebhook", """{"defaultUsername":"LB Bot"}"""),
            Chat("Default chat", 1, "Active", "identity", "SlackWebhook", """{"defaultChannel":"#alerts","iconEmoji":":warning:"}"""),
            Chat("Default chat", 1, "Active", "mocked", "Mock", "{}", hasSecret: false),
        ];
        _factory.Api.ChatLogs =
        [
            new(Guid.NewGuid(), "default", "Chat", "-1001", "Sent", "Telegram accepted (message 5)", 42, new DateTimeOffset(2026, 11, 11, 8, 30, 5, TimeSpan.Zero)),
            new(Guid.NewGuid(), "default", "Chat", "-1001", "Failed", "Telegram rejected the bot token (401).", null, new DateTimeOffset(2026, 11, 11, 8, 20, 0, TimeSpan.Zero)),
        ];
    }

    private static ChannelConfigurationResponse Chat(string name, int version, string status, string project, string provider, string settings, bool hasSecret = true) =>
        WebFactory.Config(name, version, status, project: project, channel: "Chat", provider: provider, hasSecret: hasSecret) with { Settings = JsonDocument.Parse(settings).RootElement.Clone() };

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "no anti-forgery token on the page");
        return match.Groups[1].Value;
    }

    private static List<KeyValuePair<string, string>> FormFields(string token, Dictionary<string, string>? changes = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Form.ProjectCode"] = "default",
            ["Form.Name"] = "Default chat",
            ["Form.ChannelType"] = "Chat",
            ["Form.ProviderType"] = "TelegramBot",
            ["Form.TelegramChatId"] = "-1001",
            ["Form.TelegramParseMode"] = "PlainText",
            ["Form.Secret"] = Mask,
        };
        foreach (var (key, value) in changes ?? []) fields[key] = value;
        return [.. fields];
    }

    private async Task<(HttpClient Client, string Token, string Html)> OpenAsync(string url = "/Chat")
    {
        var client = NewClient();
        var html = await client.GetStringAsync(url);
        return (client, TokenIn(html), html);
    }

    private static async Task<JsonElement> PostTestAsync(HttpClient client, string token, Dictionary<string, string>? changes = null, bool withHeader = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Chat?handler=SendTest") { Content = new FormUrlEncodedContent(FormFields(token, changes)) };
        if (withHeader) request.Headers.Add("RequestVerificationToken", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ------------------------------------------------------------------------------------------------ the form

    [Fact]
    public async Task ThePage_HasTheProvidersAndTheFieldsOfEach()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Contains("Tin Nhắn &amp; Chat", html);
        foreach (var provider in new[] { "TelegramBot", "DiscordWebhook", "SlackWebhook", "Mock" })
            Assert.Contains($"<option value=\"{provider}\"", html);
        foreach (var id in new[] { "Form_TelegramChatId", "Form_TelegramParseMode", "Form_DiscordUsername", "Form_DiscordAvatarUrl", "Form_SlackChannel", "Form_SlackIcon", "Form_Secret", "send-test-button", "save-draft", "save-activate" })
            Assert.Contains($"id=\"{id}\"", html);
    }

    [Fact]
    public async Task TheSecretIsNeverOnThePage_OnlyTheMask()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Contains($"value=\"{Mask}\"", html);
        Assert.Contains("type=\"password\"", html);
        Assert.DoesNotContain("fake-token", html);
    }

    [Fact]
    public async Task EachProject_ShowsItsOwnProviderAndSettings()
    {
        var (_, _, lb) = await OpenAsync("/Chat?project=linguabridge");
        var (_, _, id) = await OpenAsync("/Chat?project=identity");

        Assert.Matches("""<option value="DiscordWebhook"[^>]*selected""", lb);
        Assert.Contains("LB Bot", lb);
        Assert.Matches("""<option value="SlackWebhook"[^>]*selected""", id);
        Assert.Contains("#alerts", id);
        Assert.Contains(":warning:", id);
    }

    [Fact]
    public async Task AProjectWithoutAConfiguration_SaysItUsesTheDefaultOne()
    {
        var (_, _, html) = await OpenAsync("/Chat?project=brandnew");

        Assert.Contains("chưa có cấu hình chat", html);
        Assert.Contains("brandnew", html);
    }

    [Fact]
    public async Task TheFormShowsTheNewestVersion_EvenWhenItIsAnUnactivatedDraft()
    {
        _factory.Api.Items = [.. _factory.Api.Items,
            Chat("Default chat", 3, "Draft", "default", "TelegramBot", """{"defaultChatId":"-3003","parseMode":"MarkdownV2"}""")];

        var (_, _, html) = await OpenAsync();

        Assert.Contains("-3003", html);
        Assert.DoesNotContain("value=\"-1001\"", html);
        Assert.Contains("id=\"live-version\"", html);   // and it says which version is live
        Assert.Contains("v2", html);
    }

    [Fact]
    public async Task ChatConfigurationsSavedUnderTheOlderTelegramChannel_AreListedToo()
    {
        _factory.Api.Items = [Chat("Alerts bot", 1, "Active", "legacy", "TelegramBot", """{"defaultChatId":"-77"}""") with { ChannelType = "Telegram" }];

        var (_, _, html) = await OpenAsync("/Chat?project=legacy");

        Assert.Contains("Alerts bot", html);
        Assert.Contains("-77", html);
        Assert.Contains("name=\"Form.ChannelType\"", html);
        Assert.Contains("value=\"Telegram\"", html);
    }

    [Fact]
    public async Task TheDeliveryLogs_AreShownWithoutAnyMessageText()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Contains("Telegram accepted (message 5)", html);
        Assert.Contains("42 ms", html);
        Assert.Contains("data-status=\"Failed\"", html);
        Assert.Contains("chat-logs:default:20", string.Join('|', _factory.Api.Calls));
    }

    [Fact]
    public async Task WhenTheApiIsDown_ThePageStillRenders_WithAWarning()
    {
        _factory.Api.Failure = new HttpRequestException("connection refused");

        var response = await NewClient().GetAsync("/Chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"load-error\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheOlderTelegramAddress_RedirectsToChat_KeepingTheProject()
    {
        var response = await NewClient().GetAsync("/Telegram?project=linguabridge");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Chat", response.Headers.Location!.OriginalString);
        Assert.Contains("linguabridge", response.Headers.Location.OriginalString);
    }

    [Fact]
    public async Task TheSidebar_HasTheChatEntry()
    {
        var html = await NewClient().GetStringAsync("/");

        Assert.Contains("Tin Nhắn &amp; Chat<small>Telegram · Discord · Slack</small>", html);
        Assert.Contains("href=\"/Chat\"", html);
    }

    // ------------------------------------------------------------------------------------------------ saving

    [Fact]
    public async Task SaveDraft_PostsTheFormValues_AndDoesNotActivate()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/Chat?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new() { ["Form.TelegramChatId"] = "-555", ["Form.Secret"] = Mask })));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(_factory.Api.LastSave);
        Assert.Equal(("Chat", "TelegramBot"), (_factory.Api.LastSave!.ChannelType, _factory.Api.LastSave.ProviderType));
        Assert.Equal("-555", _factory.Api.LastSave.Settings!.Value.GetProperty("defaultChatId").GetString());
        Assert.Null(_factory.Api.LastSave.Secret);   // the mask means "keep the stored secret"
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("activate:"));
    }

    [Fact]
    public async Task SaveAndActivate_SavesThenActivates_AndATypedSecretIsSent()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/Chat?handler=SaveActivate", new FormUrlEncodedContent(FormFields(token, new() { ["Form.Secret"] = FakeChatSecrets.TelegramToken("page") })));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(FakeChatSecrets.TelegramToken("page"), _factory.Api.LastSave!.Secret);
        var calls = _factory.Api.Calls.ToList();
        Assert.True(calls.FindIndex(c => c.StartsWith("save:")) < calls.FindIndex(c => c.StartsWith("activate:")));
    }

    [Fact]
    public async Task FieldsOfAnotherProvider_AreNotSaved()
    {
        var (client, token, _) = await OpenAsync();

        await client.PostAsync("/Chat?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new()
        {
            ["Form.ProviderType"] = "SlackWebhook", ["Form.SlackChannel"] = "#ops", ["Form.TelegramChatId"] = "-1001", ["Form.DiscordUsername"] = "x",
        })));

        var settings = _factory.Api.LastSave!.Settings!.Value;
        Assert.Equal("#ops", settings.GetProperty("defaultChannel").GetString());
        Assert.False(settings.TryGetProperty("defaultChatId", out _));
        Assert.False(settings.TryGetProperty("defaultUsername", out _));
    }

    [Fact]
    public async Task ASaveThatTheApiRefuses_ShowsItsReason_AndDoesNotCrash()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.Failure = new SharedServicesApiException(400, "Validation", new Dictionary<string, string[]> { ["Secret"] = ["The webhook must point to discord.com."] });

        var save = await client.PostAsync("/Chat?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token)));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        _factory.Api.Failure = null;
        var next = await client.GetStringAsync(save.Headers.Location!.OriginalString);

        Assert.Contains("id=\"flash-error\"", next);
        Assert.Contains("must point to discord.com", next);
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryToken_IsRefused_AndNothingIsCalled()
    {
        var client = NewClient();
        await client.GetAsync("/Chat");
        _factory.Api.Calls.Clear();

        var response = await client.PostAsync("/Chat?handler=SaveDraft", new FormUrlEncodedContent(FormFields("wrong")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("save:") || c.StartsWith("activate:"));
    }

    // ------------------------------------------------------------------------------------------------ Send Test

    [Fact]
    public async Task ATypedSecret_IsTriedFromMemory_WithoutAConfigurationId()
    {
        var (client, token, _) = await OpenAsync();

        var result = await PostTestAsync(client, token, new() { ["Form.Secret"] = FakeChatSecrets.TelegramToken("typed"), ["Form.TestRecipient"] = "-42", ["Form.TestMessage"] = "hi" });

        Assert.True(result.GetProperty("success").GetBoolean());
        var sent = _factory.Api.LastChatTestRequest!;
        Assert.Null(sent.ConfigurationId);
        Assert.Equal(("TelegramBot", FakeChatSecrets.TelegramToken("typed"), "-42", "hi"), (sent.ProviderType, sent.Secret, sent.Recipient, sent.Message));
        Assert.DoesNotContain(FakeChatSecrets.TelegramToken("typed"), result.GetRawText());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("save:") || c.StartsWith("activate:"));
    }

    [Fact]
    public async Task WithTheMaskAndUnchangedSettings_TheSavedVersionIsTested()
    {
        var (client, token, _) = await OpenAsync();
        var newest = _factory.Api.Items.First(i => i.ProjectCode == "default" && i.Version == 2);

        await PostTestAsync(client, token);

        var sent = _factory.Api.LastChatTestRequest!;
        Assert.Equal(newest.Id, sent.ConfigurationId);
        Assert.Null(sent.Secret);
        Assert.Null(sent.ProviderType);
    }

    [Fact]
    public async Task WithTheMaskButChangedSettings_NothingIsSent_AndTheUserIsToldToRetypeTheSecret()
    {
        var (client, token, _) = await OpenAsync();

        var result = await PostTestAsync(client, token, new() { ["Form.TelegramChatId"] = "-12345" });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Bot Token", result.GetProperty("message").GetString());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-chat:"));
    }

    [Fact]
    public async Task WithTheMaskButAnotherProvider_NothingIsSent_BecauseTheStoredSecretBelongsToTheOldOne()
    {
        var (client, token, _) = await OpenAsync();

        var result = await PostTestAsync(client, token, new() { ["Form.ProviderType"] = "DiscordWebhook" });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Webhook URL", result.GetProperty("message").GetString());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-chat:"));
    }

    [Fact]
    public async Task TheMockProvider_NeedsNoSecret()
    {
        var (client, token, _) = await OpenAsync();

        var result = await PostTestAsync(client, token, new() { ["Form.ProviderType"] = "Mock", ["Form.Secret"] = string.Empty });

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("Mock", _factory.Api.LastChatTestRequest!.ProviderType);
    }

    [Fact]
    public async Task AFailedTest_ShowsTheProvidersAnswer()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.ChatTestResponse = new SendTestChatMessageResponse(false, "Telegram rejected the bot token (401).", 80, "TelegramBot", false, null);

        var result = await PostTestAsync(client, token, new() { ["Form.Secret"] = FakeChatSecrets.TelegramToken("bad") });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("rejected the bot token", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheTestHandler_NeedsTheAntiForgeryToken()
    {
        var (client, _, _) = await OpenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Chat?handler=SendTest") { Content = new FormUrlEncodedContent(FormFields("wrong")) };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-chat:"));
    }
}

/// <summary>The portal against the real API: the page and the service together, including a project that already has an active version.</summary>
public class ChatPageEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Mask = "••••••••";

    private static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpClient> AdminApiAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.ChatSend)).PlainKey);

    private static string TokenIn(string html) => Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""").Groups[1].Value;

    private static string Attribute(string html, string id)
    {
        var tag = Regex.Match(html, $"<(?:input|select)[^>]*id=\"{id}\"[^>]*>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(tag);
        return WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
    }

    private static List<KeyValuePair<string, string>> FormAsLoaded(string html, string project) =>
    [
        new("__RequestVerificationToken", TokenIn(html)),
        new("Form.ProjectCode", project),
        new("Form.Name", Attribute(html, "Form_Name")),
        new("Form.ChannelType", Regex.Match(html, "name=\"Form.ChannelType\"[^>]*value=\"([^\"]*)\"").Groups[1].Value),
        new("Form.ProviderType", Regex.Match(Regex.Match(html, "<select[^>]*id=\"Form_ProviderType\"[^>]*>(.*?)</select>", RegexOptions.Singleline).Groups[1].Value, "<option value=\"([^\"]*)\" selected").Groups[1].Value),
        new("Form.TelegramChatId", Attribute(html, "Form_TelegramChatId")),
        new("Form.TelegramParseMode", "PlainText"),
        new("Form.Secret", Attribute(html, "Form_Secret")),
    ];

    private static List<KeyValuePair<string, string>> With(List<KeyValuePair<string, string>> fields, params (string Key, string Value)[] changes)
    {
        var map = fields.ToDictionary(f => f.Key, f => f.Value);
        foreach (var (key, value) in changes) map[key] = value;
        return [.. map];
    }

    [Fact]
    public async Task SaveDraftThenActivate_ActivatesTheDraftsValues_AndTheInheritedToken_OnAProjectThatAlreadyHasAnActiveVersion()
    {
        var project = NewProject();
        var admin = await AdminApiAsync();
        var storedToken = FakeChatSecrets.TelegramToken("e2e-stored");

        // The project already has an ACTIVE version: chat -1001 with the stored token.
        var created = await admin.PostAsJsonAsync("/api/v1/channel-configurations", new
        {
            projectCode = project, channelType = "Chat", providerType = "TelegramBot", name = "Default chat", settings = new { defaultChatId = "-1001" }, secret = storedToken,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var firstId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/channel-configurations/{firstId}/activate", null)).StatusCode);

        using var portal = new PortalOnRealApiFactory(admin);
        var browser = portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        // 1. Open the page, change only the chat id (the secret stays the mask), Save draft.
        var html = await browser.GetStringAsync($"/Chat?project={project}");
        Assert.Equal(Mask, Attribute(html, "Form_Secret"));
        var draft = await browser.PostAsync("/Chat?handler=SaveDraft", new FormUrlEncodedContent(With(FormAsLoaded(html, project), ("Form.TelegramChatId", "-2002"))));
        Assert.Equal(HttpStatusCode.Redirect, draft.StatusCode);

        // 2. The page now shows the DRAFT (not the active version), so what is shown is what Save & activate will activate.
        html = await browser.GetStringAsync($"/Chat?project={project}");
        Assert.Equal("-2002", Attribute(html, "Form_TelegramChatId"));
        Assert.Contains("id=\"live-version\"", html);

        // 3. Save & activate straight from what is shown.
        var activated = await browser.PostAsync("/Chat?handler=SaveActivate", new FormUrlEncodedContent(FormAsLoaded(html, project)));
        Assert.Equal(HttpStatusCode.Redirect, activated.StatusCode);

        // 4. The live version has the draft's chat id AND the token that was stored.
        factory.Chat.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1}}") };
        factory.Chat.Calls.Clear();
        var send = await factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, ApiScopes.ChatSend)).PlainKey)
            .PostAsJsonAsync("/api/v1/chat/send", new { text = "after activation" });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var (request, body) = Assert.Single(factory.Chat.Calls);
        Assert.Contains($"/bot{storedToken}/sendMessage", request.RequestUri!.AbsoluteUri);
        Assert.Contains("-2002", body);
        Assert.DoesNotContain("-1001", body);
    }

    [Fact]
    public async Task APortalSendTest_FromMemory_LeavesEverySavedVersionUntouched()
    {
        var project = NewProject();
        var admin = await AdminApiAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/channel-configurations", new
        {
            projectCode = project, channelType = "Chat", providerType = "TelegramBot", name = "Default chat", settings = new { defaultChatId = "-1001" }, secret = FakeChatSecrets.TelegramToken("e2e-keep"),
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/v1/channel-configurations/{id}/activate", null);
        var before = await factory.WithDbAsync(db => db.ChannelConfigurations.Where(c => c.ProjectCode == project).Select(c => new { c.Id, c.Version, c.Status, c.SettingsJson, c.EncryptedSecret, c.LastTestStatus }).ToListAsync());

        using var portal = new PortalOnRealApiFactory(admin);
        var browser = portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await browser.GetStringAsync($"/Chat?project={project}");
        var fields = With(FormAsLoaded(html, project), ("Form.TelegramChatId", "-77"), ("Form.Secret", FakeChatSecrets.TelegramToken("e2e-typed")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Chat?handler=SendTest") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("RequestVerificationToken", TokenIn(html));

        var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("success").GetBoolean());
        var after = await factory.WithDbAsync(db => db.ChannelConfigurations.Where(c => c.ProjectCode == project).Select(c => new { c.Id, c.Version, c.Status, c.SettingsJson, c.EncryptedSecret, c.LastTestStatus }).ToListAsync());
        Assert.Equal(before, after);
    }
}
