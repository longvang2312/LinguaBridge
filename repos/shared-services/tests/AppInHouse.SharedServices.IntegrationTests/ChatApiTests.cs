using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.IntegrationTests;

public class ChatApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Configs = "/api/v1/channel-configurations";
    private const string Send = "/api/v1/chat/send";
    private const string Test = "/api/v1/chat/test";
    private const string OldTest = "/api/v1/telegram/test";
    private const string Logs = "/api/v1/chat/delivery-logs";

    private static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpClient> AdminAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.ChatSend)).PlainKey);

    private async Task<HttpClient> ProjectClientAsync(string project, params string[] scopes) =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, scopes)).PlainKey);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static async Task<JsonElement> SaveAsync(HttpClient admin, string project, string provider, object settings, string? secret = null, bool activate = true, string channel = "Chat")
    {
        var response = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = channel, providerType = provider, name = "Chat", settings, secret });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await ReadAsync(response);
        if (activate) Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Configs}/{saved.GetProperty("id").GetGuid()}/activate", null)).StatusCode);
        return saved;
    }

    private static string Telegram(string suffix = "0123456789") => FakeChatSecrets.TelegramToken(suffix);

    // ------------------------------------------------------------------------------------------------ sending

    [Fact]
    public async Task ASend_ThroughTheMockProvider_Delivers_AndWritesTheLog()
    {
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "Mock", new { });
        var client = await ProjectClientAsync(project, ApiScopes.ChatSend, ApiScopes.ConfigRead);

        var response = await client.PostAsJsonAsync(Send, new { text = "Disk almost full" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(project, body.GetProperty("configurationProject").GetString());
        var log = await factory.WithDbAsync(db => db.DeliveryLogs.SingleAsync(l => l.Id == body.GetProperty("deliveryLogId").GetGuid()));
        Assert.Equal((project, ChannelType.Chat, DeliveryStatus.Sent), (log.ProjectCode, log.ChannelType, log.Status));
    }

    [Fact]
    public async Task ASend_ThroughTelegram_CallsTheBotApi_WithTheStoredToken_AndEscapesTheText()
    {
        var project = NewProject();
        var token = Telegram("send1");
        await SaveAsync(await AdminAsync(), project, "TelegramBot", new { defaultChatId = "-1001" }, token);
        var client = await ProjectClientAsync(project, ApiScopes.ChatSend);
        factory.Chat.Calls.Clear();

        var response = await client.PostAsJsonAsync(Send, new { text = "Build #5 (ok)!", format = "PlainText" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (request, body) = Assert.Single(factory.Chat.Calls);
        Assert.Contains($"/bot{token}/sendMessage", request.RequestUri!.AbsoluteUri);
        Assert.Contains("-1001", body);
        Assert.Contains("Build \\\\#5 \\\\(ok\\\\)\\\\!", body);   // MarkdownV2-escaped inside the JSON string
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASend_ThroughDiscordAndSlack_PostsToTheStoredWebhook()
    {
        var admin = await AdminAsync();
        var discord = NewProject();
        var slack = NewProject();
        await SaveAsync(admin, discord, "DiscordWebhook", new { }, FakeChatSecrets.DiscordWebhook("a1"));
        await SaveAsync(admin, slack, "SlackWebhook", new { }, FakeChatSecrets.SlackWebhook("a2"));
        factory.Chat.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        factory.Chat.Calls.Clear();

        var d = await (await ProjectClientAsync(discord, ApiScopes.ChatSend)).PostAsJsonAsync(Send, new { text = "@everyone hi" });
        var s = await (await ProjectClientAsync(slack, ApiScopes.ChatSend)).PostAsJsonAsync(Send, new { text = "a < b & c" });

        Assert.Equal(HttpStatusCode.OK, d.StatusCode);
        Assert.Equal(HttpStatusCode.OK, s.StatusCode);
        Assert.Equal(2, factory.Chat.Calls.Count);
        Assert.Equal("discord.com", factory.Chat.Calls[0].Request.RequestUri!.Host);
        Assert.Contains("\"parse\":[]", factory.Chat.Calls[0].Body);
        Assert.Equal("hooks.slack.com", factory.Chat.Calls[1].Request.RequestUri!.Host);
        Assert.Equal("a &lt; b &amp; c", JsonDocument.Parse(factory.Chat.Calls[1].Body).RootElement.GetProperty("text").GetString());   // escaped for Slack, then JSON-encoded
        factory.Chat.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":77}}") };
    }

    [Fact]
    public async Task AProviderThatRefuses_Is502_AndTheLogIsFailed_WithoutTheToken()
    {
        var project = NewProject();
        var token = Telegram("refuse");
        await SaveAsync(await AdminAsync(), project, "TelegramBot", new { defaultChatId = "-1001" }, token);
        factory.Chat.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized\"}") };
        var client = await ProjectClientAsync(project, ApiScopes.ChatSend);

        var response = await client.PostAsJsonAsync(Send, new { text = "hi" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
        var log = await factory.WithDbAsync(db => db.DeliveryLogs.SingleAsync(l => l.ProjectCode == project));
        Assert.Equal(DeliveryStatus.Failed, log.Status);
        Assert.DoesNotContain(token, log.ProviderResponse ?? string.Empty);
        factory.Chat.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":77}}") };
    }

    [Fact]
    public async Task NoConfiguration_Is409()
    {
        var client = await ProjectClientAsync(NewProject(), ApiScopes.ChatSend);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Send, new { text = "hi" })).StatusCode);
    }

    [Fact]
    public async Task AProjectWithoutItsOwn_UsesTheDefaultProjectsChatConfiguration()
    {
        var admin = await AdminAsync();
        await SaveAsync(admin, "default", "Mock", new { });
        var client = await ProjectClientAsync(NewProject(), ApiScopes.ChatSend);

        var response = await client.PostAsJsonAsync(Send, new { text = "hi" });

        Assert.Equal("default", (await ReadAsync(response)).GetProperty("configurationProject").GetString());
    }

    [Fact]
    public async Task AConfigurationSavedUnderTheOlderTelegramChannel_StillSends()
    {
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "TelegramBot", new { defaultChatId = "-1001" }, Telegram("legacy"), channel: "Telegram");
        var client = await ProjectClientAsync(project, ApiScopes.ChatSend);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Send, new { text = "hi" })).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ access

    [Fact]
    public async Task WithoutAKey_401_WithoutTheScope_403_AndTheOlderTelegramScopeIsNotEnough()
    {
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "Mock", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).PostAsJsonAsync(Send, new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ProjectClientAsync(project, ApiScopes.ConfigRead)).PostAsJsonAsync(Send, new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ProjectClientAsync(project, ApiScopes.EmailSend)).PostAsJsonAsync(Send, new { text = "hi" })).StatusCode);
    }

    [Fact]
    public async Task AKeyCannotSendForAnotherProject_ButTheDefaultProjectsKeyCan()
    {
        var other = NewProject();
        var mine = NewProject();
        var admin = await AdminAsync();
        await SaveAsync(admin, other, "Mock", new { });
        var client = await ProjectClientAsync(mine, ApiScopes.ChatSend);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Send, new { text = "hi", projectCode = other })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(Send, new { text = "hi", projectCode = other })).StatusCode);
    }

    [Fact]
    public async Task TheTestEndpoint_NeedsConfigWrite_OnBothAddresses()
    {
        var reader = await ProjectClientAsync(NewProject(), ApiScopes.ChatSend, ApiScopes.ConfigRead);

        foreach (var address in new[] { Test, OldTest })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(address, new { providerType = "Mock" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).PostAsJsonAsync(address, new { providerType = "Mock" })).StatusCode);
        }
    }

    [Fact]
    public async Task TheLogs_AreOnlyThoseOfTheKeysProject_AndHoldNoToken()
    {
        var admin = await AdminAsync();
        var a = NewProject();
        var b = NewProject();
        var token = Telegram("logs1");
        await SaveAsync(admin, a, "TelegramBot", new { defaultChatId = "-1001" }, token);
        await SaveAsync(admin, b, "Mock", new { });
        var clientA = await ProjectClientAsync(a, ApiScopes.ChatSend, ApiScopes.ConfigRead);
        await clientA.PostAsJsonAsync(Send, new { text = "Very private text 9911" });
        await (await ProjectClientAsync(b, ApiScopes.ChatSend)).PostAsJsonAsync(Send, new { text = "other" });

        var mine = await (await clientA.GetAsync(Logs)).Content.ReadAsStringAsync();
        var foreign = await clientA.GetAsync($"{Logs}?projectCode={b}");

        Assert.Contains(a, mine);
        Assert.DoesNotContain(b, mine);
        Assert.DoesNotContain("private text", mine);
        Assert.DoesNotContain(token, mine);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Contains(b, await (await admin.GetAsync($"{Logs}?projectCode={b}")).Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------------------------------------ validation

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankText_Is400(string text)
    {
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "Mock", new { });

        Assert.Equal(HttpStatusCode.BadRequest, (await (await ProjectClientAsync(project, ApiScopes.ChatSend)).PostAsJsonAsync(Send, new { text })).StatusCode);
    }

    [Theory]
    [InlineData("TelegramBot", "not a token")]
    [InlineData("DiscordWebhook", "https://169.254.169.254/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("DiscordWebhook", "http://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("SlackWebhook", "https://127.0.0.1/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("SlackWebhook", "https://hooks.slack.com@evil.example.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    public async Task SavingASecretOfTheWrongShapeOrToAnotherServer_Is400_AndNothingIsStored(string provider, string secret)
    {
        var project = NewProject();

        var response = await (await AdminAsync()).PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Chat", providerType = provider, name = "Chat", settings = new { }, secret });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync());
        Assert.False(await factory.WithDbAsync(db => db.ChannelConfigurations.AnyAsync(c => c.ProjectCode == project)));
    }

    [Fact]
    public async Task ChangingTheProvider_WithoutANewSecret_Is400_AndWithOne_Succeeds()
    {
        var project = NewProject();
        var admin = await AdminAsync();
        await SaveAsync(admin, project, "TelegramBot", new { defaultChatId = "-1001" }, Telegram("chg"));

        var refused = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Chat", providerType = "DiscordWebhook", name = "Chat", settings = new { } });
        var accepted = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Chat", providerType = "DiscordWebhook", name = "Chat", settings = new { }, secret = FakeChatSecrets.DiscordWebhook("chg") });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ P0: Send Test never touches saved configurations

    private sealed record Row(Guid Id, int Version, ConfigurationStatus Status, string Settings, string? Secret, DateTimeOffset? TestedAt, TestStatus? TestStatus);

    private async Task<List<Row>> SnapshotAsync(string project) =>
        await factory.WithDbAsync(async db => (await db.ChannelConfigurations.Where(c => c.ProjectCode == project).OrderBy(c => c.Version).ToListAsync())
            .Select(c => new Row(c.Id, c.Version, c.Status, c.SettingsJson, c.EncryptedSecret, c.LastTestedAt, c.LastTestStatus)).ToList());

    [Theory]
    [InlineData(Test)]
    [InlineData(OldTest)]
    public async Task ATestFromMemory_ChangesNothingThatIsSaved_AndSendsWithTheTypedSecret(string address)
    {
        var project = NewProject();
        var admin = await AdminAsync();
        await SaveAsync(admin, project, "TelegramBot", new { defaultChatId = "-1001" }, Telegram("stored"));
        var before = await SnapshotAsync(project);
        var configCount = await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync());
        factory.Chat.Calls.Clear();
        var typed = Telegram("typed");

        var response = await admin.PostAsJsonAsync(address, new { providerType = "TelegramBot", settings = new { defaultChatId = "-42" }, secret = typed, message = "hello" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.False(body.GetProperty("usedSavedConfiguration").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("configurationId").ValueKind);
        Assert.DoesNotContain(typed, await response.Content.ReadAsStringAsync());
        var (request, sent) = Assert.Single(factory.Chat.Calls);
        Assert.Contains($"/bot{typed}/sendMessage", request.RequestUri!.AbsoluteUri);   // the typed token, never the stored one
        Assert.Contains("-42", sent);

        Assert.Equal(before, await SnapshotAsync(project));                       // the saved versions are byte-for-byte what they were
        Assert.Equal(configCount, await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync()));   // and nothing new was created
    }

    [Fact]
    public async Task ATestFromMemory_WithAProjectThatHasNoConfiguration_CreatesNoRow()
    {
        var admin = await AdminAsync();
        var count = await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync());

        var response = await admin.PostAsJsonAsync(Test, new { providerType = "Mock" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(count, await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync()));
    }

    [Fact]
    public async Task ATestFromMemory_NeverBorrowsTheStoredSecret()
    {
        var project = NewProject();
        var admin = await AdminAsync();
        await SaveAsync(admin, project, "TelegramBot", new { defaultChatId = "-1001" }, Telegram("stored2"));
        factory.Chat.Calls.Clear();

        var response = await admin.PostAsJsonAsync(Test, new { providerType = "TelegramBot", settings = new { defaultChatId = "-1" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Chat.Calls);
    }

    [Fact]
    public async Task ATestOfASavedVersion_UsesItsSecret_AndRecordsTheResultOnThatVersionOnly()
    {
        var project = NewProject();
        var admin = await AdminAsync();
        var stored = Telegram("stored3");
        var first = await SaveAsync(admin, project, "TelegramBot", new { defaultChatId = "-1001" }, stored);
        var draft = await SaveAsync(admin, project, "TelegramBot", new { defaultChatId = "-2002" }, activate: false);
        factory.Chat.Calls.Clear();

        var response = await admin.PostAsJsonAsync(Test, new { configurationId = draft.GetProperty("id").GetGuid() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("usedSavedConfiguration").GetBoolean());
        var (request, sent) = Assert.Single(factory.Chat.Calls);
        Assert.Contains($"/bot{stored}/sendMessage", request.RequestUri!.AbsoluteUri);   // inherited by the draft
        Assert.Contains("-2002", sent);
        var rows = await SnapshotAsync(project);
        Assert.Equal(TestStatus.Success, rows.Single(r => r.Id == draft.GetProperty("id").GetGuid()).TestStatus);
        Assert.Equal(TestStatus.Untested, rows.Single(r => r.Id == first.GetProperty("id").GetGuid()).TestStatus);
        Assert.Equal(ConfigurationStatus.Active, rows.Single(r => r.Id == first.GetProperty("id").GetGuid()).Status);   // testing the draft activated nothing
    }

    [Fact]
    public async Task AFailedSavedTest_IsRecordedFailed_AndStillAnswers200()
    {
        var project = NewProject();
        var admin = await AdminAsync();
        var saved = await SaveAsync(admin, project, "TelegramBot", new { defaultChatId = "-1001" }, Telegram("fail"));
        factory.Chat.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized\"}") };

        var response = await admin.PostAsJsonAsync(Test, new { configurationId = saved.GetProperty("id").GetGuid() });
        factory.Chat.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":77}}") };

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ReadAsync(response)).GetProperty("success").GetBoolean());
        Assert.Equal(TestStatus.Failed, (await SnapshotAsync(project)).Single().TestStatus);
    }

    [Fact]
    public async Task TheTestEndpoint_AnswersWithoutTheSecret_AndAnUnknownIdIs404()
    {
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Test, new { configurationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Test, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Test, new { providerType = "Smtp" })).StatusCode);
    }

    [Theory]
    [InlineData("DiscordWebhook", "https://169.254.169.254/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("SlackWebhook", "https://hooks.slack.com.evil.example.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("TelegramBot", "../../etc/passwd:token")]
    public async Task ATestFromMemory_AgainstAForbiddenAddress_IsRefused_AndNothingLeavesTheService(string provider, string secret)
    {
        var admin = await AdminAsync();
        factory.Chat.Calls.Clear();

        var response = await admin.PostAsJsonAsync(Test, new { providerType = provider, secret });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Chat.Calls);
        Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync());
    }
}

public class ChatApiDevelopmentTests(DevelopmentApiFactory factory) : IClassFixture<DevelopmentApiFactory>
{
    [Fact]
    public async Task InDevelopment_ABadRequest_IsStill400_NotACrash()
    {
        var (plain, _) = await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ChatSend, ApiScopes.ConfigWrite);
        var client = factory.ClientWithKey(plain);

        var malformed = await client.PostAsync("/api/v1/chat/send", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        var blank = await client.PostAsJsonAsync("/api/v1/chat/send", new { text = "" });
        var badTest = await client.PostAsJsonAsync("/api/v1/chat/test", new { providerType = "DiscordWebhook", secret = "https://evil.example.com/x" });

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badTest.StatusCode);
    }
}
