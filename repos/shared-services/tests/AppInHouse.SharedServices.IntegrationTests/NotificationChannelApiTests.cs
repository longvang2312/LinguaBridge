using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>
/// The bots (Telegram, Discord, Slack) as channels of the notification service: <c>/api/v1/notifications/channels</c>. The older
/// <c>/api/v1/chat/*</c> addresses are aliases that must behave exactly the same, so most checks run on both.
/// </summary>
public class NotificationChannelApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Channels = "/api/v1/notifications/channels";

    private static readonly string[] SendAddresses = [Channels + "/send"];
    private static readonly string[] TestAddresses = [Channels + "/test"];
    private static readonly string[] LogAddresses = [Channels + "/delivery-logs"];

    private static async Task<JsonElement> SaveBotAsync(HttpClient client, string project, string provider, object? settings = null, string? secret = null, string name = "Bot", bool activate = true)
    {
        var response = await client.PostAsJsonAsync(Channels, new { projectCode = project, providerType = provider, name, settings = settings ?? new { }, secret });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await ReadAsync(response);
        if (activate) Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Channels}/{saved.GetProperty("id").GetGuid()}/activate", null)).StatusCode);
        return saved;
    }

    private Task<HttpClient> ManagerAsync(string project) => ProjectClientAsync(factory, project, ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.ChatSend);

    // ------------------------------------------------------------------------------------------------ managing the channels

    [Fact]
    public async Task ABotChannel_IsSaved_Activated_AndListed_AsAChannelOfTheNotificationService()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);

        var saved = await SaveBotAsync(client, project, "Mock", activate: false);
        Assert.Equal(("Chat", "Draft", 1), (saved.GetProperty("channelType").GetString(), saved.GetProperty("status").GetString(), saved.GetProperty("version").GetInt32()));
        var activated = await client.PostAsync($"{Channels}/{saved.GetProperty("id").GetGuid()}/activate", null);
        var listed = (await ReadAsync(await client.GetAsync($"{Channels}?projectCode={project}"))).EnumerateArray().ToList();
        var one = await ReadAsync(await client.GetAsync($"{Channels}/{saved.GetProperty("id").GetGuid()}"));

        Assert.Equal("Active", (await ReadAsync(activated)).GetProperty("status").GetString());
        var item = Assert.Single(listed);
        Assert.Equal(("Chat", "Mock", "Active"), (item.GetProperty("channelType").GetString(), item.GetProperty("providerType").GetString(), item.GetProperty("status").GetString()));
        Assert.Equal(saved.GetProperty("id").GetGuid(), one.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task TheStoreIsTheSame_AsTheGenericConfigurationEndpoints()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        var saved = await SaveBotAsync(client, project, "Mock");

        var generic = (await ReadAsync(await client.GetAsync($"/api/v1/channel-configurations?projectCode={project}&channelType=Chat"))).EnumerateArray().Single();

        Assert.Equal(saved.GetProperty("id").GetGuid(), generic.GetProperty("id").GetGuid());
        Assert.Equal(1, await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync(c => c.ProjectCode == project && c.ChannelType == ChannelType.Chat)));
    }

    [Fact]
    public async Task TheKindFilter_SeparatesBotsFromEmail_AndUnknownKindsAre400()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        await SaveBotAsync(client, project, "Mock");
        var email = await client.PostAsJsonAsync(Channels, new { projectCode = project, channelType = "Email", providerType = "Mock", name = "Mail", settings = new { senderEmail = "noreply@example.com" } });
        Assert.Equal(HttpStatusCode.Created, email.StatusCode);

        async Task<List<string?>> TypesAsync(string query) =>
            (await ReadAsync(await client.GetAsync($"{Channels}?projectCode={project}{query}"))).EnumerateArray().Select(c => c.GetProperty("channelType").GetString()).Order().ToList();

        Assert.Equal(["Chat"], await TypesAsync(string.Empty));
        Assert.Equal(["Chat"], await TypesAsync("&kind=bot"));
        Assert.Equal(["Email"], await TypesAsync("&kind=email"));
        Assert.Equal(["Chat", "Email"], await TypesAsync("&kind=all"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Channels}?projectCode={project}&kind=storage")).StatusCode);
    }

    [Fact]
    public async Task OnlyNotificationChannels_AreHandledHere_NotStorage()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        var storage = await client.PostAsJsonAsync("/api/v1/channel-configurations", new { projectCode = project, channelType = "Storage", providerType = "Mock", name = "Files", settings = new { } });
        var storageId = (await ReadAsync(storage)).GetProperty("id").GetGuid();

        var save = await client.PostAsJsonAsync(Channels, new { projectCode = project, channelType = "Storage", providerType = "Mock", name = "Files2", settings = new { } });
        var get = await client.GetAsync($"{Channels}/{storageId}");
        var activate = await client.PostAsync($"{Channels}/{storageId}/activate", null);
        var rollback = await client.PostAsJsonAsync($"{Channels}/rollback", new { projectCode = project, channelType = "Storage", name = "Files" });
        var listed = await ReadAsync(await client.GetAsync($"{Channels}?projectCode={project}&kind=all"));

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, activate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rollback.StatusCode);
        Assert.Empty(listed.EnumerateArray());
        Assert.Equal("Draft", (await factory.WithDbAsync(db => db.ChannelConfigurations.SingleAsync(c => c.Id == storageId))).Status.ToString());   // not activated through here
    }

    [Fact]
    public async Task ABotSecret_IsStoredProtected_AndNeverComesBack()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        var token = FakeChatSecrets.TelegramToken("notif-" + Guid.NewGuid().ToString("N")[..6]);

        var saved = await client.PostAsJsonAsync(Channels, new { projectCode = project, providerType = "TelegramBot", name = "Alerts", settings = new { defaultChatId = "-1001" }, secret = token });
        var body = await saved.Content.ReadAsStringAsync();
        var listed = await (await client.GetAsync($"{Channels}?projectCode={project}")).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.DoesNotContain(token, body + listed);
        Assert.Contains("\"hasSecret\":true", body);
        var stored = await factory.WithDbAsync(db => db.ChannelConfigurations.SingleAsync(c => c.ProjectCode == project));
        Assert.DoesNotContain(token, stored.EncryptedSecret ?? string.Empty);
    }

    [Fact]
    public async Task ABadBotConfiguration_IsRefusedBeforeItIsSaved()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);

        var wrongProvider = await client.PostAsJsonAsync(Channels, new { projectCode = project, providerType = "Smtp", name = "Bot", settings = new { } });
        var badToken = await client.PostAsJsonAsync(Channels, new { projectCode = project, providerType = "TelegramBot", name = "Bot", settings = new { }, secret = "not a token" });

        Assert.Equal(HttpStatusCode.BadRequest, wrongProvider.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badToken.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync(c => c.ProjectCode == project)));
    }

    [Fact]
    public async Task ARollback_GoesBackToTheEarlierVersion()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        var v1 = await SaveBotAsync(client, project, "Mock", new { });
        await SaveBotAsync(client, project, "Mock", new { defaultChatId = "-2" });   // version 2, active

        var rolled = await client.PostAsJsonAsync($"{Channels}/rollback", new { projectCode = project, name = "Bot" });

        Assert.Equal(HttpStatusCode.OK, rolled.StatusCode);
        var body = await ReadAsync(rolled);
        Assert.Equal(("Active", 1), (body.GetProperty("status").GetString(), body.GetProperty("version").GetInt32()));
        Assert.Equal(v1.GetProperty("id").GetGuid(), body.GetProperty("id").GetGuid());
    }

    // ------------------------------------------------------------------------------------------------ who may do what

    [Fact]
    public async Task EveryEndpoint_NeedsItsScope_AndAProjectCannotTouchAnother()
    {
        var mine = NewProject();
        var other = NewProject();
        var owner = await ManagerAsync(mine);
        var theirs = await SaveBotAsync(await ManagerAsync(other), other, "Mock");
        var readOnly = await ProjectClientAsync(factory, mine, ApiScopes.ConfigRead);
        var sendOnly = await ProjectClientAsync(factory, mine, ApiScopes.ChatSend);
        var theirId = theirs.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync(Channels)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sendOnly.GetAsync(Channels)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(Channels, new { projectCode = mine, providerType = "Mock", name = "x", settings = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsJsonAsync(Channels + "/send", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sendOnly.PostAsJsonAsync(Channels + "/test", new { providerType = "Mock" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{Channels}?projectCode={other}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{Channels}/{theirId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync($"{Channels}/{theirId}/activate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(Channels, new { projectCode = other, providerType = "Mock", name = "x", settings = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"{Channels}/rollback", new { projectCode = other, name = "Bot" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(Channels + "/send", new { text = "hi", projectCode = other })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(Channels + "/test", new { configurationId = theirId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{Channels}/delivery-logs?projectCode={other}")).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ sending and testing: new addresses and aliases alike

    [Theory]
    [InlineData(0)]
    public async Task ASend_Delivers_AndLogs_(int address)
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        await SaveBotAsync(client, project, "Mock");

        var response = await client.PostAsJsonAsync(SendAddresses[address], new { text = $"Disk almost full via {address}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(("Sent", true, project), (body.GetProperty("status").GetString(), body.GetProperty("success").GetBoolean(), body.GetProperty("configurationProject").GetString()));
        Assert.Contains(MockChat(factory).SentMessages, m => m.ProjectCode == project && m.Text.Contains($"via {address}"));
        var log = await factory.WithDbAsync(db => db.DeliveryLogs.SingleAsync(l => l.Id == body.GetProperty("deliveryLogId").GetGuid()));
        Assert.Equal((project, ChannelType.Chat, DeliveryStatus.Sent), (log.ProjectCode, log.ChannelType, log.Status));
    }

    [Theory]
    [InlineData(0)]
    public async Task WithoutAConfiguration_TheSend_Says409_AndANonOwnerProject_403(int address)
    {
        var project = NewProject();
        var other = NewProject();
        var client = await ProjectClientAsync(factory, project, ApiScopes.ChatSend);
        var noConfig = await client.PostAsJsonAsync(SendAddresses[address], new { text = "hi" });
        var foreign = await client.PostAsJsonAsync(SendAddresses[address], new { text = "hi", projectCode = other });

        Assert.Equal(HttpStatusCode.Conflict, noConfig.StatusCode);   // no bot for the project, and none for the platform project to fall back on
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/v1/chat/send")]
    [InlineData("POST", "/api/v1/chat/messages")]
    [InlineData("POST", "/api/v1/chat/test")]
    [InlineData("POST", "/api/v1/telegram/test")]
    [InlineData("GET", "/api/v1/chat/delivery-logs")]
    public async Task TheOldChatAliases_AreGone(string method, string address)
    {
        var client = await AdminAsync(factory);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), address) { Content = JsonContent.Create(new { text = "hi", providerType = "Mock" }) });

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {address} answered {(int)response.StatusCode}");
    }

    [Fact]
    public async Task ASendThroughTelegram_CallsTheBotApi_WithTheStoredToken()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        var token = FakeChatSecrets.TelegramToken("addr-" + Guid.NewGuid().ToString("N")[..6]);
        await SaveBotAsync(client, project, "TelegramBot", new { defaultChatId = "-1001" }, token);
        factory.Chat.Calls.Clear();

        foreach (var address in SendAddresses)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(address, new { text = "Build #1 *passed*" })).StatusCode);

        Assert.Single(factory.Chat.Calls);
        Assert.All(factory.Chat.Calls, call => Assert.Contains("/bot" + token + "/sendMessage", call.Request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task ATest_FromMemory_Works_AndSavesNothing()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);

        foreach (var address in TestAddresses)
        {
            var response = await client.PostAsJsonAsync(address, new { providerType = "Mock", settings = new { } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.True(body.GetProperty("success").GetBoolean());
            Assert.False(body.GetProperty("usedSavedConfiguration").GetBoolean());
        }

        Assert.Equal(0, await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync(c => c.ProjectCode == project)));
    }

    [Fact]
    public async Task ATest_OfASavedChannel_UpdatesItsLastTestResult()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        var saved = await SaveBotAsync(client, project, "Mock");

        var response = await client.PostAsJsonAsync(Channels + "/test", new { configurationId = saved.GetProperty("id").GetGuid() });

        Assert.True((await ReadAsync(response)).GetProperty("usedSavedConfiguration").GetBoolean());
        Assert.Equal(TestStatus.Success, (await factory.WithDbAsync(db => db.ChannelConfigurations.SingleAsync(c => c.Id == saved.GetProperty("id").GetGuid()))).LastTestStatus);
    }

    [Fact]
    public async Task TheDeliveryLogs_HoldNoTextOrSecret()
    {
        var project = NewProject();
        var client = await ManagerAsync(project);
        await SaveBotAsync(client, project, "Mock");
        await client.PostAsJsonAsync(SendAddresses[0], new { text = "a very private sentence" });

        var bodies = new List<string>();
        foreach (var address in LogAddresses)
            bodies.Add(await (await client.GetAsync($"{address}?projectCode={project}&limit=5")).Content.ReadAsStringAsync());

        Assert.Contains("\"status\":\"Sent\"", bodies[0]);
        Assert.DoesNotContain("private sentence", bodies[0]);
    }

    // ------------------------------------------------------------------------------------------------ the notice of a conversation uses the same bots

    [Fact]
    public async Task ABotSavedHere_IsTheOneTheOfflineNoticeOfAConversationUses()
    {
        var project = NewProject();
        var chatId = "chat-" + Guid.NewGuid().ToString("N")[..8];
        var client = await ManagerAsync(project);
        await SaveBotAsync(client, project, "Mock");
        var key = await ChatApi.ProjectKeyAsync(factory, project, ApiScopes.ChatSend);
        var lan = ChatApi.User(factory, key, "lan", "Lan");
        var room = ChatApi.IdOf(await ChatApi.CreateDirectAsync(lan, "minh", chatId: chatId));

        await ChatApi.SendAsync(lan, room, "ping");

        Assert.True(await UntilAsync(() => MockChat(factory).SentMessages.Any(m => m.Recipient == chatId)));
    }
}
