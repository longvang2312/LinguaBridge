using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.SharedServices.UnitTests;

public class NotificationBotDispatcherTests
{
    private sealed class Fixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeChatProvider Telegram { get; } = new(ProviderType.TelegramBot);
        public FakeChatProvider Discord { get; } = new(ProviderType.DiscordWebhook);
        public FakeChatProvider Slack { get; } = new(ProviderType.SlackWebhook);
        public FakeChatProvider Mock { get; } = new(ProviderType.Mock);
        public FakeDeliveryLogRepository Logs { get; } = new();
        public FakeUnitOfWork Unit { get; } = new();
        public NotificationBotDispatcher Dispatcher { get; }

        public Fixture(bool withTelegramProvider = true)
        {
            var providers = new List<FakeChatProvider> { Discord, Slack, Mock };
            if (withTelegramProvider) providers.Add(Telegram);
            Dispatcher = new NotificationBotDispatcher(Configs.Manager, providers, Logs, Unit, Configs.Clock, NullLogger<NotificationBotDispatcher>.Instance);
        }

        public async Task<ChannelConfigurationDto> ActivateAsync(
            string project, ProviderType provider, string settings = "{}", string? secret = null, ChannelType channel = ChannelType.Chat, string name = "Chat")
        {
            var saved = await Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand(
                project, channel, provider, name, settings, secret ?? (ProviderCatalog.RequiresSecret(provider) ? FakeChatSecrets.For(provider) : null)));
            return await Configs.Manager.ActivateAsync(saved.Id);
        }
    }

    private static SendBotMessageCommand Hello(string project = "linguabridge", string text = "Disk almost full") => new(project, text);

    // ------------------------------------------------------------------------------------------------ routing

    [Theory]
    [InlineData(ProviderType.TelegramBot)]
    [InlineData(ProviderType.DiscordWebhook)]
    [InlineData(ProviderType.SlackWebhook)]
    [InlineData(ProviderType.Mock)]
    public async Task EachProviderType_IsRoutedToItsOwnProvider(ProviderType type)
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", type);

        var result = await f.Dispatcher.SendAsync(Hello());

        Assert.True(result.Success);
        var all = new[] { f.Telegram, f.Discord, f.Slack, f.Mock };
        Assert.Single(all.Single(p => p.Type == type).Sent);
        Assert.Equal(1, all.Sum(p => p.Sent.Count));
        Assert.Equal(("linguabridge", 1), (result.ConfigurationProject, result.ConfigurationVersion));
    }

    [Fact]
    public async Task TheProvider_IsGivenTheDecryptedSecret_ForThatCallOnly()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.DiscordWebhook);

        await f.Dispatcher.SendAsync(Hello());

        var (message, config) = Assert.Single(f.Discord.Sent);
        Assert.Equal(FakeChatSecrets.DiscordWebhook, config.Secret);
        Assert.Equal(("Disk almost full", "linguabridge"), (message.Text, message.ProjectCode));
    }

    [Fact]
    public async Task TheRecipientIsTrimmed_AndAnEmptyOneMeansTheConfigurationsDefault()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.TelegramBot);

        await f.Dispatcher.SendAsync(new SendBotMessageCommand("linguabridge", "a", Recipient: "  -1009  "));
        await f.Dispatcher.SendAsync(new SendBotMessageCommand("linguabridge", "b", Recipient: "   "));

        Assert.Equal("-1009", f.Telegram.Sent[0].Message.Recipient);
        Assert.Null(f.Telegram.Sent[1].Message.Recipient);
    }

    [Fact]
    public async Task AFormatThatWasNotGiven_StaysNull_SoTheConfigurationDecides_AndAGivenOneIsPassedOn()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.TelegramBot);

        await f.Dispatcher.SendAsync(new SendBotMessageCommand("linguabridge", "a"));
        await f.Dispatcher.SendAsync(new SendBotMessageCommand("linguabridge", "b", Format: BotFormat.MarkdownV2));

        Assert.Null(f.Telegram.Sent[0].Message.Format);
        Assert.Equal(BotFormat.MarkdownV2, f.Telegram.Sent[1].Message.Format);
    }

    [Fact]
    public async Task AProjectWithoutItsOwnConfiguration_FallsBackToTheDefaultProjects()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock);

        var result = await f.Dispatcher.SendAsync(Hello("shop"));

        Assert.True(result.Success);
        Assert.Equal("default", result.ConfigurationProject);
        Assert.Equal("shop", Assert.Single(f.Logs.Rows).ProjectCode);   // the log says who asked, not whose settings were used
    }

    [Fact]
    public async Task TheProjectsOwnConfiguration_WinsOverTheDefault()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock);
        await f.ActivateAsync("shop", ProviderType.SlackWebhook);

        var result = await f.Dispatcher.SendAsync(Hello("shop"));

        Assert.Equal("shop", result.ConfigurationProject);
        Assert.Single(f.Slack.Sent);
        Assert.Empty(f.Mock.Sent);
    }

    [Fact]
    public async Task AConfigurationSavedUnderTheOlderTelegramChannel_StillSends()
    {
        var f = new Fixture();
        await f.ActivateAsync("legacy", ProviderType.TelegramBot, channel: ChannelType.Telegram, name: "Alerts bot");

        var result = await f.Dispatcher.SendAsync(Hello("legacy"));

        Assert.True(result.Success);
        Assert.Single(f.Telegram.Sent);
        Assert.Equal(ChannelType.Telegram, Assert.Single(f.Logs.Rows).ChannelType);   // logged under the channel the configuration lives in
    }

    [Fact]
    public async Task WithBothAChatAndAnOlderTelegramConfiguration_TheChatOneIsUsed()
    {
        var f = new Fixture();
        await f.ActivateAsync("both", ProviderType.TelegramBot, channel: ChannelType.Telegram, name: "Old");
        await f.ActivateAsync("both", ProviderType.SlackWebhook, channel: ChannelType.Chat, name: "New");

        await f.Dispatcher.SendAsync(Hello("both"));

        Assert.Single(f.Slack.Sent);
        Assert.Empty(f.Telegram.Sent);
    }

    [Fact]
    public async Task OnlyTheActiveVersion_IsUsed_NotADraft()
    {
        var f = new Fixture();
        await f.ActivateAsync("shop", ProviderType.Mock);
        await f.Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("shop", ChannelType.Chat, ProviderType.DiscordWebhook, "Chat", "{}", FakeChatSecrets.DiscordWebhook));   // a draft

        await f.Dispatcher.SendAsync(Hello("shop"));

        Assert.Single(f.Mock.Sent);
        Assert.Empty(f.Discord.Sent);
    }

    [Fact]
    public async Task NoConfigurationAnywhere_IsReported_AndLogged()
    {
        var f = new Fixture();

        var result = await f.Dispatcher.SendAsync(Hello("shop"));

        Assert.False(result.Success);
        Assert.Equal(BotFailure.NoConfiguration, result.Failure);
        Assert.Contains("shop", result.Error);
        Assert.Contains("default", result.Error);
        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal((DeliveryStatus.Failed, ChannelType.Chat), (log.Status, log.ChannelType));
    }

    [Fact]
    public async Task AnEmailConfiguration_IsNotAChatConfiguration()
    {
        var f = new Fixture();
        await f.Configs.Manager.ActivateAsync((await f.Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("default", ChannelType.Email, ProviderType.Mock, "Mail", "{}", null))).Id);

        var result = await f.Dispatcher.SendAsync(Hello("default"));

        Assert.Equal(BotFailure.NoConfiguration, result.Failure);
    }

    [Fact]
    public async Task AConfigurationWhoseProviderIsNotAvailable_FailsClearly()
    {
        var f = new Fixture(withTelegramProvider: false);
        await f.ActivateAsync("default", ProviderType.TelegramBot);

        var result = await f.Dispatcher.SendAsync(Hello("default"));

        Assert.Equal(BotFailure.ProviderFailure, result.Failure);
        Assert.Contains("TelegramBot", result.Error);
        Assert.Equal(DeliveryStatus.Failed, Assert.Single(f.Logs.Rows).Status);
    }

    // ------------------------------------------------------------------------------------------------ the delivery log

    [Fact]
    public async Task ASuccessfulSend_IsLogged_WithTheTargetStatusResponseAndLatency()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.TelegramBot);

        var result = await f.Dispatcher.SendAsync(Hello());

        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal(("linguabridge", ChannelType.Chat, "-1001", DeliveryStatus.Sent), (log.ProjectCode, log.ChannelType, log.Recipient, log.Status));
        Assert.Equal("Accepted (42)", log.ProviderResponse);
        Assert.NotNull(log.LatencyMs);
        Assert.Equal(result.DeliveryLogId, log.Id);
        Assert.Equal(1, f.Unit.Saves);
    }

    [Fact]
    public async Task WhenTheProviderNamesNoTarget_TheLogSaysDefault()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.DiscordWebhook);
        f.Discord.SendResult = new BotSendResult(true, ProviderResponse: "Discord accepted (200)");   // a webhook posts where it was made for

        await f.Dispatcher.SendAsync(Hello());

        Assert.Equal("(default)", Assert.Single(f.Logs.Rows).Recipient);
    }

    [Fact]
    public async Task AProviderFailure_IsLoggedAsFailed_WithACleanedMessage_NeverTheSecret()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.DiscordWebhook);
        f.Discord.SendResult = new BotSendResult(false, Error: $"Discord refused {FakeChatSecrets.DiscordWebhook}\nmore");

        var result = await f.Dispatcher.SendAsync(Hello());

        Assert.False(result.Success);
        Assert.Equal(BotFailure.ProviderFailure, result.Failure);
        Assert.DoesNotContain(FakeChatSecrets.DiscordWebhook, result.Error);
        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal(DeliveryStatus.Failed, log.Status);
        Assert.DoesNotContain(FakeChatSecrets.DiscordWebhook, log.ProviderResponse);
        Assert.DoesNotContain('\n', log.ProviderResponse!);
    }

    [Fact]
    public async Task AProviderThatThrows_IsAFailedSend_NotACrash()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.TelegramBot);
        f.Telegram.Throw = new InvalidOperationException($"kaboom {FakeChatSecrets.TelegramToken}");

        var result = await f.Dispatcher.SendAsync(Hello());

        Assert.False(result.Success);
        Assert.DoesNotContain(FakeChatSecrets.TelegramToken, result.Error);
        Assert.Equal(DeliveryStatus.Failed, Assert.Single(f.Logs.Rows).Status);
    }

    [Fact]
    public async Task TheLog_HoldsNoMessageTextNoTokenAndNoWebhookAddress()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.SlackWebhook);
        f.Slack.SendResult = new BotSendResult(true, ProviderResponse: "Slack accepted (200)", Target: "#alerts");

        await f.Dispatcher.SendAsync(new SendBotMessageCommand("linguabridge", "Confidential incident 7781 details"));

        var everything = string.Join('|', typeof(DeliveryLog).GetProperties().Select(p => p.GetValue(f.Logs.Rows[0])?.ToString() ?? string.Empty));
        Assert.DoesNotContain("Confidential", everything);
        Assert.DoesNotContain("incident", everything);
        Assert.DoesNotContain("hooks.slack.com", everything);
        Assert.DoesNotContain("fakewebhooktokenfortests", everything);
        Assert.Contains("#alerts", everything);
        Assert.DoesNotContain(typeof(DeliveryLog).GetProperties(), p => p.Name is "Text" or "Message" or "Body" or "Secret" or "Token" or "Webhook");
    }

    [Fact]
    public async Task ACredentialThatCannotBeRead_IsAFailedSend_WithAMessageToReEnterIt()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.TelegramBot);
        var row = f.Configs.Repository.Rows.Single();
        row.GetType().GetProperty(nameof(ChannelConfiguration.EncryptedSecret))!.SetValue(row, "not-protected-by-this-key-ring");

        var result = await f.Dispatcher.SendAsync(Hello());

        Assert.False(result.Success);
        Assert.Contains("cannot be read", result.Error);
        Assert.DoesNotContain("not-protected", result.Error);
        Assert.Empty(f.Telegram.Sent);
        Assert.Equal(DeliveryStatus.Failed, Assert.Single(f.Logs.Rows).Status);
    }

    [Fact]
    public async Task Recent_ListsChatAndOlderTelegramDeliveries_NotEmail_NewestFirst_WithinTheLimit()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock);
        foreach (var (project, text) in new[] { ("a", "1"), ("b", "2"), ("a", "3"), ("a", "4") })
        {
            f.Configs.Clock.Advance(TimeSpan.FromMinutes(1));
            f.Mock.SendResult = new BotSendResult(true, Target: text);
            await f.Dispatcher.SendAsync(Hello(project, "msg"));
        }

        f.Logs.Rows.Add(DeliveryLog.Create("a", ChannelType.Telegram, "legacy", DeliveryStatus.Sent, "ok", 5, f.Configs.Clock.GetUtcNow().AddMinutes(10)));   // an older Telegram row
        f.Logs.Rows.Add(DeliveryLog.Create("a", ChannelType.Email, "mail@x.co", DeliveryStatus.Sent, "ok", 5, f.Configs.Clock.GetUtcNow().AddMinutes(20)));   // an email row

        var all = await f.Dispatcher.RecentAsync(null, 20);
        var onlyA = await f.Dispatcher.RecentAsync("A", 2);

        Assert.Equal(["legacy", "4", "3", "2", "1"], all.Select(l => l.Recipient));
        Assert.DoesNotContain(all, l => l.ChannelType == ChannelType.Email);
        Assert.Equal(["legacy", "4"], onlyA.Select(l => l.Recipient));
        Assert.Single(await f.Dispatcher.RecentAsync(null, 0));              // the limit is clamped to at least 1
        Assert.True((await f.Dispatcher.RecentAsync(null, 100000)).Count <= 100);
    }

    // ------------------------------------------------------------------------------------------------ the request

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankMessage_IsRefused_AndNothingIsSentOrLogged(string text)
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock);

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(Hello("default", text)));

        Assert.Contains("Text", error.Errors.Keys);
        Assert.Empty(f.Mock.Sent);
        Assert.Empty(f.Logs.Rows);
    }

    [Fact]
    public async Task AMessageLongerThanTheLimit_IsRefused_AndOneAtTheLimitIsNot()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock);

        await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(Hello("default", new string('x', NotificationBotDispatcher.MaxTextLength + 1))));
        var ok = await f.Dispatcher.SendAsync(Hello("default", new string('x', NotificationBotDispatcher.MaxTextLength)));

        Assert.True(ok.Success);
        Assert.Single(f.Mock.Sent);
    }

    [Fact]
    public async Task ABadFormat_ARecipientWithControlCharacters_AndTooMuchMetadata_AreRefused()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock);
        string[] Keys(ValidationException e) => [.. e.Errors.Keys];

        Assert.Contains("Format", Keys(await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(new SendBotMessageCommand("default", "x", Format: (BotFormat)99)))));
        Assert.Contains("Recipient", Keys(await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(new SendBotMessageCommand("default", "x", Recipient: "-100\nBcc")))));
        Assert.Contains("Recipient", Keys(await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(new SendBotMessageCommand("default", "x", Recipient: new string('9', 101))))));
        Assert.Contains("Metadata", Keys(await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(
            new SendBotMessageCommand("default", "x", Metadata: Enumerable.Range(0, 21).ToDictionary(i => $"k{i}", _ => "v"))))));
        Assert.Empty(f.Mock.Sent);
    }
}

public class ChatTestServiceTests
{
    private sealed class Fixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeChatProvider Telegram { get; } = new(ProviderType.TelegramBot);
        public FakeChatProvider Discord { get; } = new(ProviderType.DiscordWebhook);
        public FakeChatProvider Slack { get; } = new(ProviderType.SlackWebhook);
        public FakeChatProvider Mock { get; } = new(ProviderType.Mock);
        public NotificationBotTestService Service { get; }

        public Fixture() =>
            Service = new NotificationBotTestService(Configs.Manager, [Telegram, Discord, Slack, Mock], Configs.Audit, Configs.UnitOfWork, NullLogger<NotificationBotTestService>.Instance);

        public async Task<ChannelConfigurationDto> SaveAsync(ProviderType provider, string settings = "{}", string? secret = null, ChannelType channel = ChannelType.Chat) =>
            await Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("default", channel, provider, "Chat", settings, secret ?? FakeChatSecrets.For(provider)));
    }

    // ------------------------------------------------------------------------------------------------ from memory: nothing is stored

    [Fact]
    public async Task AnUnsavedConfiguration_IsTriedFromMemory_WithTheSecretFromTheRequest()
    {
        var f = new Fixture();

        var result = await f.Service.TestAsync(new TestBotCommand("-1007", "a test", ProviderType: ProviderType.TelegramBot, SettingsJson: """{"defaultChatId":"-1"}""", Secret: FakeChatSecrets.TelegramToken));

        Assert.True(result.Success);
        Assert.False(result.UsedSavedConfiguration);
        Assert.Null(result.ConfigurationId);
        var (config, recipient, text) = Assert.Single(f.Telegram.Tested);
        Assert.Equal(("-1007", "a test"), (recipient, text));
        Assert.Equal(FakeChatSecrets.TelegramToken, config.Secret);
        Assert.Equal("-1", config.Settings.GetProperty("defaultChatId").GetString());
    }

    [Fact]
    public async Task AnUnsavedTest_CreatesNothing_ChangesNothing_AndOnlyLeavesAnAuditEntry()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.TelegramBot);
        await f.Configs.Manager.ActivateAsync(saved.Id);
        var snapshot = f.Configs.Repository.Rows.Select(r => (r.Id, r.Version, r.Status, r.SettingsJson, r.EncryptedSecret, r.LastTestedAt, r.LastTestStatus)).ToList();
        var auditBefore = f.Configs.Audit.Entries.Count;

        await f.Service.TestAsync(new TestBotCommand(ProviderType: ProviderType.DiscordWebhook, Secret: FakeChatSecrets.DiscordWebhook));

        Assert.Equal(snapshot, f.Configs.Repository.Rows.Select(r => (r.Id, r.Version, r.Status, r.SettingsJson, r.EncryptedSecret, r.LastTestedAt, r.LastTestStatus)).ToList());
        Assert.Equal(ConfigurationStatus.Active, f.Configs.Repository.Rows.Single().Status);
        var audit = Assert.Single(f.Configs.Audit.Entries.Skip(auditBefore));
        Assert.Equal("SendTest", audit.Action);
        Assert.DoesNotContain("discord.com", audit.DetailsJson);
        Assert.Contains("\"Saved\":false", audit.DetailsJson);
    }

    [Fact]
    public async Task WithoutARecipientOrAMessage_TheDefaultsAreUsed()
    {
        var f = new Fixture();

        await f.Service.TestAsync(new TestBotCommand(ProviderType: ProviderType.Mock));

        var (_, recipient, text) = Assert.Single(f.Mock.Tested);
        Assert.Equal(string.Empty, recipient);
        Assert.Equal(NotificationBotTestService.DefaultTestMessage, text);
    }

    [Fact]
    public async Task AFailedTest_IsAnAnswer_WithTheSecretRemovedFromTheMessage()
    {
        var f = new Fixture();
        f.Discord.TestResult = new BotTestResult(false, $"refused {FakeChatSecrets.DiscordWebhook}", 30);

        var result = await f.Service.TestAsync(new TestBotCommand(ProviderType: ProviderType.DiscordWebhook, Secret: FakeChatSecrets.DiscordWebhook));

        Assert.False(result.Success);
        Assert.DoesNotContain(FakeChatSecrets.DiscordWebhook, result.Message);
        Assert.Equal(30, result.LatencyMs);
    }

    [Fact]
    public async Task AProviderThatThrows_DuringATest_IsAFailedTest()
    {
        var f = new Fixture();
        f.Telegram.Throw = new InvalidOperationException($"explosion {FakeChatSecrets.TelegramToken}");

        var result = await f.Service.TestAsync(new TestBotCommand(ProviderType: ProviderType.TelegramBot, Secret: FakeChatSecrets.TelegramToken));

        Assert.False(result.Success);
        Assert.DoesNotContain(FakeChatSecrets.TelegramToken, result.Message);
    }

    [Theory]
    [InlineData(ProviderType.TelegramBot)]
    [InlineData(ProviderType.DiscordWebhook)]
    [InlineData(ProviderType.SlackWebhook)]
    public async Task TheRealProviders_NeedTheirSecretInTheRequest_AndAStoredOneIsNotBorrowed(ProviderType provider)
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(provider);
        await f.Configs.Manager.ActivateAsync(saved.Id);

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestBotCommand(ProviderType: provider)));
        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestBotCommand(ProviderType: provider, Secret: "••••••••")));   // the mask is not a secret

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.All(new[] { f.Telegram, f.Discord, f.Slack }, p => Assert.Empty(p.Tested));
    }

    [Theory]
    [InlineData(ProviderType.TelegramBot, "not a token")]
    [InlineData(ProviderType.DiscordWebhook, "https://169.254.169.254/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData(ProviderType.DiscordWebhook, "http://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData(ProviderType.SlackWebhook, "https://127.0.0.1/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData(ProviderType.SlackWebhook, "https://hooks.slack.com.evil.example.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    public async Task ASecretOfTheWrongShape_OrAWebhookToAnotherServer_IsRefused_BeforeAnythingIsTried(ProviderType provider, string secret)
    {
        var f = new Fixture();

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestBotCommand(ProviderType: provider, Secret: secret)));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.DoesNotContain(secret, string.Join(' ', error.Errors.SelectMany(e => e.Value)));
        Assert.All(new[] { f.Telegram, f.Discord, f.Slack }, p => Assert.Empty(p.Tested));
    }

    [Fact]
    public async Task SettingsWithASecretInside_AndAProviderThatIsNotAChatProvider_AreRefused()
    {
        var f = new Fixture();

        var withSecret = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(
            new TestBotCommand(ProviderType: ProviderType.TelegramBot, SettingsJson: """{"botToken":"oops"}""", Secret: FakeChatSecrets.TelegramToken)));
        var wrongKind = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestBotCommand(ProviderType: ProviderType.Smtp)));
        var noProvider = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestBotCommand()));

        Assert.Contains("SettingsJson", withSecret.Errors.Keys);
        Assert.Contains("ProviderType", wrongKind.Errors.Keys);
        Assert.Contains("ProviderType", noProvider.Errors.Keys);
    }

    [Fact]
    public async Task ATooLongRecipientOrMessage_IsRefused()
    {
        var f = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestBotCommand(new string('9', 101), ProviderType: ProviderType.Mock)));
        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestBotCommand(TestMessage: new string('x', NotificationBotTestService.MaxTestMessageLength + 1), ProviderType: ProviderType.Mock)));
    }

    // ------------------------------------------------------------------------------------------------ a saved version

    [Fact]
    public async Task ASavedVersion_IsTestedWithItsStoredSecret_AndTheResultIsRecorded()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.SlackWebhook);   // a draft: it can be tested before it is activated
        f.Configs.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await f.Service.TestAsync(new TestBotCommand(ConfigurationId: saved.Id));

        Assert.True(result.Success);
        Assert.True(result.UsedSavedConfiguration);
        Assert.Equal(saved.Id, result.ConfigurationId);
        Assert.Equal(FakeChatSecrets.SlackWebhook, Assert.Single(f.Slack.Tested).Config.Secret);
        var row = f.Configs.Repository.Rows.Single();
        Assert.Equal((TestStatus.Success, f.Configs.Clock.GetUtcNow()), (row.LastTestStatus, row.LastTestedAt));
        Assert.Equal(ConfigurationStatus.Draft, row.Status);   // testing does not activate
    }

    [Fact]
    public async Task AFailedSavedTest_IsRecordedAsFailed()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.TelegramBot);
        f.Telegram.TestResult = new BotTestResult(false, "refused", 9);

        var result = await f.Service.TestAsync(new TestBotCommand(ConfigurationId: saved.Id));

        Assert.False(result.Success);
        Assert.Equal(TestStatus.Failed, f.Configs.Repository.Rows.Single().LastTestStatus);
        Assert.Contains(f.Configs.Audit.Entries, e => e.Action == "SendTest" && e.DetailsJson.Contains("Failed") && !e.DetailsJson.Contains("fake-token"));
    }

    [Fact]
    public async Task AConfigurationSavedUnderTheOlderTelegramChannel_CanBeTested()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.TelegramBot, channel: ChannelType.Telegram);

        var result = await f.Service.TestAsync(new TestBotCommand(ConfigurationId: saved.Id));

        Assert.True(result.Success);
        Assert.Single(f.Telegram.Tested);
    }

    [Fact]
    public async Task ASavedTest_IgnoresAnySettingsInTheSameRequest_SoTheStoredSecretGoesOnlyWhereItWasSaved()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.SlackWebhook, """{"defaultChannel":"#saved"}""");

        await f.Service.TestAsync(new TestBotCommand(ConfigurationId: saved.Id,
            ProviderType: ProviderType.SlackWebhook, SettingsJson: """{"defaultChannel":"#other"}""", Secret: FakeChatSecrets.SlackWebhook));

        var (config, _, _) = Assert.Single(f.Slack.Tested);
        Assert.Equal("#saved", config.Settings.GetProperty("defaultChannel").GetString());
        Assert.Equal(FakeChatSecrets.SlackWebhook, config.Secret);
    }

    [Fact]
    public async Task AnUnknownId_IsNotFound_AndAnEmailConfigurationIsRefused()
    {
        var f = new Fixture();
        var email = await f.Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("default", ChannelType.Email, ProviderType.Mock, "Mail", "{}", null));

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.TestAsync(new TestBotCommand(ConfigurationId: Guid.NewGuid())));
        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestBotCommand(ConfigurationId: email.Id)));
    }
}

public class ChatProviderChangeTests
{
    [Fact]
    public async Task TelegramToDiscord_WithoutANewSecret_IsRefused_AndNothingIsSaved()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.TelegramBot, "Chat", "{}", FakeChatSecrets.TelegramToken));
        var rows = fixture.Repository.Rows.Count;

        var error = await Assert.ThrowsAsync<ValidationException>(() => fixture.Manager.SaveAsync(
            new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.DiscordWebhook, "Chat", "{}", Secret: null)));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.Contains("DiscordWebhook", error.Errors["Secret"][0]);
        Assert.Contains("not reused", error.Errors["Secret"][0]);
        Assert.Equal(rows, fixture.Repository.Rows.Count);
    }

    [Theory]
    [InlineData(ProviderType.TelegramBot, ProviderType.DiscordWebhook)]
    [InlineData(ProviderType.TelegramBot, ProviderType.SlackWebhook)]
    [InlineData(ProviderType.DiscordWebhook, ProviderType.SlackWebhook)]
    [InlineData(ProviderType.SlackWebhook, ProviderType.TelegramBot)]
    [InlineData(ProviderType.DiscordWebhook, ProviderType.TelegramBot)]
    public async Task EveryChangeBetweenTheRealProviders_NeedsTheNewSecret(ProviderType from, ProviderType to)
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Chat, from, "Chat", "{}", FakeChatSecrets.For(from)));

        foreach (var notASecret in new string?[] { null, "", "••••••••" })
        {
            await Assert.ThrowsAsync<ValidationException>(() => fixture.Manager.SaveAsync(
                new SaveChannelConfigurationCommand("p", ChannelType.Chat, to, "Chat", "{}", notASecret)));
        }

        var saved = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Chat, to, "Chat", "{}", FakeChatSecrets.For(to)));
        Assert.Equal(to, saved.ProviderType);
        Assert.Equal(FakeChatSecrets.For(to), fixture.Protector.Unprotect(fixture.Repository.Rows.Single(r => r.Version == 2).EncryptedSecret!));
    }

    [Fact]
    public async Task ToMock_NoSecretIsNeeded_AndTheOldOneDoesNotFollow()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.SlackWebhook, "Chat", "{}", FakeChatSecrets.SlackWebhook));

        var mock = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.Mock, "Chat", "{}", null));

        Assert.False(mock.HasSecret);
        Assert.Null(fixture.Repository.Rows.Single(r => r.Version == 2).EncryptedSecret);
    }

    [Fact]
    public async Task TheSameProvider_KeepsItsSecret_WhenNoneIsTyped()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.DiscordWebhook, "Chat", "{}", FakeChatSecrets.DiscordWebhook));

        var next = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.DiscordWebhook, "Chat", """{"defaultUsername":"Alerts"}""", null));

        Assert.True(next.HasSecret);
        Assert.Equal(FakeChatSecrets.DiscordWebhook, fixture.Protector.Unprotect(fixture.Repository.Rows.Single(r => r.Version == 2).EncryptedSecret!));
    }

    [Fact]
    public async Task ASecretOfTheWrongShape_IsRefusedOnSave_AndNothingIsStored()
    {
        var fixture = new ManagerFixture();

        var error = await Assert.ThrowsAsync<ValidationException>(() => fixture.Manager.SaveAsync(
            new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.DiscordWebhook, "Chat", "{}", "https://evil.example.com/hook")));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.Empty(fixture.Repository.Rows);
    }

    [Fact]
    public async Task TheWebhookAddress_IsStoredOnlyProtected_AndNeverShown()
    {
        var fixture = new ManagerFixture();

        var dto = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Chat, ProviderType.SlackWebhook, "Chat", "{}", FakeChatSecrets.SlackWebhook));

        var row = Assert.Single(fixture.Repository.Rows);
        Assert.DoesNotContain("hooks.slack.com", row.EncryptedSecret);
        Assert.DoesNotContain("hooks.slack.com", row.SettingsJson);
        Assert.Equal("••••••••", dto.MaskedSecret);
        Assert.DoesNotContain("hooks.slack.com", System.Text.Json.JsonSerializer.Serialize(dto));
        Assert.All(fixture.Audit.Entries, e => Assert.DoesNotContain("hooks.slack.com", e.DetailsJson));
    }
}

public class ChatClientSdkTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static (SharedServicesClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new SharedServicesClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") }), handler);
    }

    [Fact]
    public async Task SendChatMessage_PostsTheRequest_AndReadsTheAnswer()
    {
        var (client, handler) = Make(_ => Json(System.Net.HttpStatusCode.OK, """{"success":true,"status":"Sent","deliveryLogId":"7f2d6a1e-0000-0000-0000-000000000009","latencyMs":42,"configurationProject":"default","configurationVersion":3}"""));

        var response = await client.SendNotificationChannelMessageAsync(new SendBotMessageRequest("Disk almost full", Recipient: "-1001", Format: "PlainText"));

        Assert.True(response.Success);
        Assert.Equal(("Sent", 42, "default", 3), (response.Status, response.LatencyMs, response.ConfigurationProject, response.ConfigurationVersion));
        Assert.Equal(("/api/v1/notifications/channels/send", HttpMethod.Post), (handler.Requests[0].Url, handler.Requests[0].Method));
        Assert.Contains("\"text\":\"Disk almost full\"", handler.Requests[0].Body);
        Assert.Contains("\"recipient\":\"-1001\"", handler.Requests[0].Body);
        Assert.Contains("\"format\":\"PlainText\"", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Conflict, 409)]
    [InlineData(System.Net.HttpStatusCode.BadGateway, 502)]
    public async Task SendChatMessage_ThatTheServiceRefuses_ThrowsWithTheStatusAndTheDetail(System.Net.HttpStatusCode status, int code)
    {
        var (client, _) = Make(_ => Json(status, """{"title":"x","detail":"Project shop has no active chat configuration."}"""));

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.SendNotificationChannelMessageAsync(new SendBotMessageRequest("hi")));

        Assert.Equal(code, exception.StatusCode);
        Assert.Contains("shop", exception.Message);
    }

    [Fact]
    public async Task SendTestChatMessage_PostsToTheTestEndpoint_WithTheSecretAsAWriteOnlyField()
    {
        var (client, handler) = Make(_ => Json(System.Net.HttpStatusCode.OK, """{"success":false,"message":"Telegram rejected the bot token (401).","latencyMs":80,"providerType":"TelegramBot","usedSavedConfiguration":false,"configurationId":null}"""));
        using var settings = System.Text.Json.JsonDocument.Parse("""{"defaultChatId":"-1001"}""");

        var response = await client.TestNotificationChannelAsync(new TestBotChannelRequest("-1001", "hello", ProviderType: "TelegramBot", Settings: settings.RootElement.Clone(), Secret: "typed-secret"));

        Assert.False(response.Success);
        Assert.Contains("rejected the bot token", response.Message);
        Assert.Equal("/api/v1/notifications/channels/test", handler.Requests[0].Url);
        Assert.Contains("\"secret\":\"typed-secret\"", handler.Requests[0].Body);
        Assert.Contains("\"message\":\"hello\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task GetChatDeliveryLogs_BuildsTheQuery_AndReadsTheRows()
    {
        var (client, handler) = Make(_ => Json(System.Net.HttpStatusCode.OK, """[{"id":"7f2d6a1e-0000-0000-0000-000000000009","projectCode":"default","channelType":"Chat","recipient":"-1001","status":"Sent","providerResponse":"Telegram accepted (message 5)","latencyMs":12,"createdAt":"2026-11-11T08:00:00+00:00"}]"""));

        var logs = await client.GetBotDeliveryLogsAsync("default", 5);

        var log = Assert.Single(logs);
        Assert.Equal(("-1001", "Sent", "Chat", 12), (log.Recipient, log.Status, log.ChannelType, log.LatencyMs));
        Assert.Equal("/api/v1/notifications/channels/delivery-logs?limit=5&projectCode=default", handler.Requests[0].Url);

        await client.GetBotDeliveryLogsAsync();
        Assert.Equal("/api/v1/notifications/channels/delivery-logs?limit=20", handler.Requests[1].Url);
    }
}
