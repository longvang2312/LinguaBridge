using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.SharedServices.UnitTests;

public class EmailDispatcherTests
{
    private const string Password = "dispatcher-P@ss-1234";

    private sealed class Fixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeEmailProvider Smtp { get; } = new(ProviderType.Smtp);
        public FakeEmailProvider SendGrid { get; } = new(ProviderType.SendGrid);
        public FakeEmailProvider Mock { get; } = new(ProviderType.Mock);
        public FakeDeliveryLogRepository Logs { get; } = new();
        public FakeUnitOfWork Unit { get; } = new();
        public EmailDispatcher Dispatcher { get; }

        public Fixture()
        {
            Dispatcher = new EmailDispatcher(Configs.Manager, [Smtp, SendGrid, Mock], new EmailTemplateRenderer(), Logs, Unit, Configs.Clock, NullLogger<EmailDispatcher>.Instance);
        }

        public async Task ActivateAsync(string project, ProviderType provider, string settings, string? secret, string name = "Email")
        {
            var saved = await Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand(project, ChannelType.Email, provider, name, settings, secret));
            await Configs.Manager.ActivateAsync(saved.Id);
        }
    }

    private static SendEmailCommand Verification(string project = "linguabridge", string to = "user@example.com") =>
        new(project, to, Template: "EmailVerification", Variables: new Dictionary<string, string> { ["name"] = "Lan", ["code"] = "123456" });

    // ------------------------------------------------------------------------------------------------ routing

    [Fact]
    public async Task ItSendsWithTheProvider_TheProjectsActiveConfigurationNames()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.Smtp, RuntimeConfig.Smtp, Password);

        var result = await f.Dispatcher.SendAsync(Verification());

        Assert.True(result.Success);
        Assert.Equal(EmailFailure.None, result.Failure);
        var (message, config) = Assert.Single(f.Smtp.Sent);
        Assert.Equal("user@example.com", message.To);
        Assert.Contains("123456", message.HtmlBody);
        Assert.Empty(f.SendGrid.Sent);
        Assert.Empty(f.Mock.Sent);
        Assert.Equal(Password, config.Secret);           // decrypted for the provider, only for this call
        Assert.Equal(("linguabridge", 1), (result.ConfigurationProject, result.ConfigurationVersion));
    }

    [Theory]
    [InlineData(ProviderType.Smtp)]
    [InlineData(ProviderType.SendGrid)]
    [InlineData(ProviderType.Mock)]
    public async Task EachProviderType_IsRoutedToItsOwnProvider(ProviderType type)
    {
        var f = new Fixture();
        var settings = type == ProviderType.Smtp ? RuntimeConfig.Smtp : RuntimeConfig.SendGrid;
        await f.ActivateAsync("default", type, settings, type == ProviderType.Mock ? null : Password);

        await f.Dispatcher.SendAsync(Verification("default"));

        var providers = new[] { f.Smtp, f.SendGrid, f.Mock };
        Assert.Single(providers.Single(p => p.Type == type).Sent);
        Assert.Equal(1, providers.Sum(p => p.Sent.Count));
    }

    [Fact]
    public async Task AProjectWithoutItsOwnConfiguration_FallsBackToTheDefaultProjects()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);

        var result = await f.Dispatcher.SendAsync(Verification("shop"));

        Assert.True(result.Success);
        Assert.Equal("default", result.ConfigurationProject);
        Assert.Single(f.Mock.Sent);
        Assert.Equal("shop", Assert.Single(f.Logs.Rows).ProjectCode);   // the log says who asked, not whose settings were used
    }

    [Fact]
    public async Task TheProjectsOwnConfiguration_WinsOverTheDefault()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);
        await f.ActivateAsync("shop", ProviderType.Smtp, RuntimeConfig.Smtp, Password);

        var result = await f.Dispatcher.SendAsync(Verification("shop"));

        Assert.Equal("shop", result.ConfigurationProject);
        Assert.Single(f.Smtp.Sent);
        Assert.Empty(f.Mock.Sent);
    }

    [Fact]
    public async Task OnlyTheActiveVersion_IsUsed_NotADraft()
    {
        var f = new Fixture();
        await f.ActivateAsync("shop", ProviderType.Mock, "{}", null);
        await f.Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("shop", ChannelType.Email, ProviderType.Smtp, "Email", RuntimeConfig.Smtp, Password));   // a draft

        await f.Dispatcher.SendAsync(Verification("shop"));

        Assert.Single(f.Mock.Sent);
        Assert.Empty(f.Smtp.Sent);
    }

    [Fact]
    public async Task NoConfigurationAnywhere_IsReported_AndLogged()
    {
        var f = new Fixture();

        var result = await f.Dispatcher.SendAsync(Verification("shop"));

        Assert.False(result.Success);
        Assert.Equal(EmailFailure.NoConfiguration, result.Failure);
        Assert.Contains("shop", result.Error);
        Assert.Contains("default", result.Error);
        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal(DeliveryStatus.Failed, log.Status);
    }

    [Fact]
    public async Task AConfigurationWhoseProviderIsNotAvailable_FailsClearly()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Brevo, RuntimeConfig.SendGrid, Password);   // listed, not implemented

        var result = await f.Dispatcher.SendAsync(Verification("default"));

        Assert.False(result.Success);
        Assert.Equal(EmailFailure.ProviderFailure, result.Failure);
        Assert.Contains("Brevo", result.Error);
        Assert.Equal(DeliveryStatus.Failed, Assert.Single(f.Logs.Rows).Status);
    }

    // ------------------------------------------------------------------------------------------------ the delivery log

    [Fact]
    public async Task ASuccessfulSend_IsLogged_WithRecipientStatusResponseAndLatency()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.Mock, "{}", null);

        var result = await f.Dispatcher.SendAsync(Verification());

        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal(("linguabridge", ChannelType.Email, "user@example.com", DeliveryStatus.Sent), (log.ProjectCode, log.ChannelType, log.Recipient, log.Status));
        Assert.Equal("250 queued", log.ProviderResponse);
        Assert.NotNull(log.LatencyMs);
        Assert.Equal(result.DeliveryLogId, log.Id);
        Assert.Equal(f.Configs.Clock.GetUtcNow(), log.CreatedAt);
        Assert.Equal(1, f.Unit.Saves);   // the log is saved once per send
    }

    [Fact]
    public async Task AProviderFailure_IsLoggedAsFailed_WithACleanedMessage_AndNeverTheSecret()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.Smtp, RuntimeConfig.Smtp, Password);
        f.Smtp.SendResult = new EmailSendResult(false, Error: $"535 login failed for password {Password}\nmore");

        var result = await f.Dispatcher.SendAsync(Verification());

        Assert.False(result.Success);
        Assert.Equal(EmailFailure.ProviderFailure, result.Failure);
        Assert.DoesNotContain(Password, result.Error);
        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal(DeliveryStatus.Failed, log.Status);
        Assert.DoesNotContain(Password, log.ProviderResponse);
        Assert.DoesNotContain('\n', log.ProviderResponse!);
    }

    [Fact]
    public async Task AProviderThatThrows_IsAFailedSend_NotACrash()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.Smtp, RuntimeConfig.Smtp, Password);
        f.Smtp.Throw = new InvalidOperationException($"kaboom {Password}");

        var result = await f.Dispatcher.SendAsync(Verification());

        Assert.False(result.Success);
        Assert.DoesNotContain(Password, result.Error);
        Assert.Equal(DeliveryStatus.Failed, Assert.Single(f.Logs.Rows).Status);
    }

    [Fact]
    public async Task TheLog_HoldsNeitherTheSubjectNorTheBodyNorAnyCredential()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.Smtp, RuntimeConfig.Smtp, Password);

        await f.Dispatcher.SendAsync(new SendEmailCommand("linguabridge", "user@example.com", Subject: "Private subject 42", HtmlBody: "<p>Confidential body 4242</p>", TextBody: "Confidential text"));

        var everything = string.Join('|', typeof(DeliveryLog).GetProperties().Select(p => p.GetValue(f.Logs.Rows[0])?.ToString() ?? string.Empty));
        Assert.DoesNotContain("Private subject", everything);
        Assert.DoesNotContain("Confidential", everything);
        Assert.DoesNotContain(Password, everything);
        Assert.DoesNotContain("mailer", everything);   // not even the username
        Assert.DoesNotContain(typeof(DeliveryLog).GetProperties(), p => p.Name is "Subject" or "Body" or "HtmlBody" or "TextBody" or "Secret" or "Password");
    }

    [Fact]
    public async Task ACredentialThatCannotBeRead_IsAFailedSend_WithAMessageToReEnterIt()
    {
        var f = new Fixture();
        await f.ActivateAsync("linguabridge", ProviderType.Smtp, RuntimeConfig.Smtp, Password);
        f.Configs.Repository.Rows.Single().GetType().GetProperty(nameof(ChannelConfiguration.EncryptedSecret))!
            .SetValue(f.Configs.Repository.Rows.Single(), "not-protected-by-this-key-ring");

        var result = await f.Dispatcher.SendAsync(Verification());

        Assert.False(result.Success);
        Assert.Contains("cannot be read", result.Error);
        Assert.DoesNotContain("not-protected", result.Error);
        Assert.Empty(f.Smtp.Sent);
        Assert.Equal(DeliveryStatus.Failed, Assert.Single(f.Logs.Rows).Status);
    }

    [Fact]
    public async Task Recent_ListsTheNewestFirst_ForOneProject_AndAppliesTheLimit()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);
        foreach (var (project, to) in new[] { ("a", "1@x.co"), ("b", "2@x.co"), ("a", "3@x.co"), ("a", "4@x.co") })
        {
            f.Configs.Clock.Advance(TimeSpan.FromMinutes(1));
            await f.Dispatcher.SendAsync(Verification(project, to));
        }

        var all = await f.Dispatcher.RecentAsync(null, 20);
        var onlyA = await f.Dispatcher.RecentAsync("A", 2);

        Assert.Equal(["4@x.co", "3@x.co", "2@x.co", "1@x.co"], all.Select(l => l.Recipient));
        Assert.Equal(["4@x.co", "3@x.co"], onlyA.Select(l => l.Recipient));
        Assert.Single(await f.Dispatcher.RecentAsync(null, 0));                               // limit is clamped to at least 1
        Assert.True((await f.Dispatcher.RecentAsync(null, 100000)).Count <= 100);            // and at most 100
    }

    // ------------------------------------------------------------------------------------------------ the request

    [Fact]
    public async Task TheTemplate_IsRendered_BeforeItReachesTheProvider_WithHtmlEscaped()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);

        await f.Dispatcher.SendAsync(new SendEmailCommand("default", "user@example.com", Template: "PasswordReset",
            Variables: new Dictionary<string, string> { ["name"] = "<b>Lan</b>", ["link"] = "https://app.test/r?t=1" }));

        var (message, _) = Assert.Single(f.Mock.Sent);
        Assert.Equal("Khôi phục mật khẩu AppInHouse", message.Subject);
        Assert.DoesNotContain("{{", message.HtmlBody);
        Assert.Contains("&lt;b&gt;Lan&lt;/b&gt;", message.HtmlBody);
        Assert.Contains("https://app.test/r?t=1", message.TextBody);
    }

    [Fact]
    public async Task CustomContent_IsSentAsGiven()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);

        await f.Dispatcher.SendAsync(new SendEmailCommand("default", "user@example.com", Subject: "Hello", HtmlBody: "<p>Hi</p>"));

        var (message, _) = Assert.Single(f.Mock.Sent);
        Assert.Equal(("Hello", "<p>Hi</p>"), (message.Subject, message.HtmlBody));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b.co\r\nBcc: x@y.z")]
    [InlineData("a@b.co,c@d.co")]
    [InlineData("")]
    public async Task ABadRecipient_IsRefused_AndNothingIsSentOrLogged(string to)
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(Verification("default", to)));

        Assert.Contains("To", error.Errors.Keys);
        Assert.Empty(f.Mock.Sent);
        Assert.Empty(f.Logs.Rows);
    }

    [Fact]
    public async Task ARequestNeedsATemplateOrACompleteMessage_NotBoth_NotNeither()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);
        string[] Keys(ValidationException e) => [.. e.Errors.Keys];

        Assert.Contains("Template", Keys(await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(new SendEmailCommand("default", "a@b.co")))));
        Assert.Contains("Template", Keys(await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(new SendEmailCommand("default", "a@b.co", Template: "PasswordReset", Subject: "x")))));
        Assert.Contains("Subject", Keys(await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(new SendEmailCommand("default", "a@b.co", HtmlBody: "<p>x</p>")))));
        Assert.Contains("Body", Keys(await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(new SendEmailCommand("default", "a@b.co", Subject: "x")))));
        Assert.Empty(f.Mock.Sent);
    }

    [Fact]
    public async Task AMissingTemplateVariable_IsRefused_BeforeAnythingIsSent()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);

        await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(
            new SendEmailCommand("default", "a@b.co", Template: "EmailVerification", Variables: new Dictionary<string, string> { ["name"] = "Lan" })));

        Assert.Empty(f.Mock.Sent);
        Assert.Empty(f.Logs.Rows);
    }

    [Fact]
    public async Task AnOversizedBody_AndAnOversizedSubject_AreRefused()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.Mock, "{}", null);

        await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(
            new SendEmailCommand("default", "a@b.co", Subject: "x", HtmlBody: new string('x', EmailDispatcher.MaxBodyLength + 1))));
        await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(
            new SendEmailCommand("default", "a@b.co", Subject: new string('s', EmailDispatcher.MaxSubjectLength + 1), HtmlBody: "<p>x</p>")));
        Assert.Empty(f.Mock.Sent);
    }
}

public class EmailTestServiceTests
{
    private const string Password = "test-service-P@ss-5678";

    private sealed class Fixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeEmailProvider Smtp { get; } = new(ProviderType.Smtp);
        public FakeEmailProvider SendGrid { get; } = new(ProviderType.SendGrid);
        public FakeEmailProvider Mock { get; } = new(ProviderType.Mock);
        public EmailTestService Service { get; }

        public Fixture() =>
            Service = new EmailTestService(Configs.Manager, [Smtp, SendGrid, Mock], Configs.Audit, Configs.UnitOfWork, NullLogger<EmailTestService>.Instance);

        public async Task<ChannelConfigurationDto> SaveAsync(ProviderType provider, string settings, string? secret) =>
            await Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("default", ChannelType.Email, provider, "Email", settings, secret));
    }

    // ------------------------------------------------------------------------------------------------ from memory: nothing is stored

    [Fact]
    public async Task AnUnsavedConfiguration_IsTriedFromMemory_WithTheSecretFromTheRequest()
    {
        var f = new Fixture();

        var result = await f.Service.TestAsync(new TestEmailCommand("me@example.com", ProviderType: ProviderType.Smtp, SettingsJson: RuntimeConfig.Smtp, Secret: Password));

        Assert.True(result.Success);
        Assert.False(result.UsedSavedConfiguration);
        Assert.Null(result.ConfigurationId);
        var (config, recipient) = Assert.Single(f.Smtp.Tested);
        Assert.Equal("me@example.com", recipient);
        Assert.Equal(Password, config.Secret);
        Assert.Equal("203.0.113.10", config.Settings.GetProperty("host").GetString());
    }

    [Fact]
    public async Task AnUnsavedTest_CreatesNothing_ChangesNothing_AndOnlyLeavesAnAuditEntry()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.Smtp, RuntimeConfig.Smtp, Password);
        await f.Configs.Manager.ActivateAsync(saved.Id);
        var rowsBefore = f.Configs.Repository.Rows.Count;
        var snapshot = f.Configs.Repository.Rows.Select(r => (r.Id, r.Version, r.Status, r.SettingsJson, r.EncryptedSecret, r.LastTestedAt, r.LastTestStatus)).ToList();
        var auditBefore = f.Configs.Audit.Entries.Count;

        await f.Service.TestAsync(new TestEmailCommand("me@example.com", ProviderType: ProviderType.SendGrid, SettingsJson: RuntimeConfig.SendGrid, Secret: "SG-brand-new-api-key-0000"));

        Assert.Equal(rowsBefore, f.Configs.Repository.Rows.Count);                                   // no new configuration or version
        Assert.Equal(snapshot, f.Configs.Repository.Rows.Select(r => (r.Id, r.Version, r.Status, r.SettingsJson, r.EncryptedSecret, r.LastTestedAt, r.LastTestStatus)).ToList());
        Assert.Equal(ConfigurationStatus.Active, f.Configs.Repository.Rows.Single().Status);         // and the active version is untouched
        var audit = Assert.Single(f.Configs.Audit.Entries.Skip(auditBefore));
        Assert.Equal("SendTest", audit.Action);
        Assert.DoesNotContain("SG-brand-new", audit.DetailsJson);
        Assert.Contains("\"Saved\":false", audit.DetailsJson);
    }

    [Fact]
    public async Task AFailedUnsavedTest_IsAnAnswer_WithTheSecretRemovedFromTheMessage()
    {
        var f = new Fixture();
        f.Smtp.TestResult = new EmailTestResult(false, $"535 rejected {Password}", 30);

        var result = await f.Service.TestAsync(new TestEmailCommand("me@example.com", ProviderType: ProviderType.Smtp, SettingsJson: RuntimeConfig.Smtp, Secret: Password));

        Assert.False(result.Success);
        Assert.DoesNotContain(Password, result.Message);
        Assert.Equal(30, result.LatencyMs);
    }

    [Fact]
    public async Task AProviderThatThrows_DuringATest_IsAFailedTest_NotAnException()
    {
        var f = new Fixture();
        f.Smtp.Throw = new InvalidOperationException($"explosion {Password}");

        var result = await f.Service.TestAsync(new TestEmailCommand("me@example.com", ProviderType: ProviderType.Smtp, SettingsJson: RuntimeConfig.Smtp, Secret: Password));

        Assert.False(result.Success);
        Assert.DoesNotContain(Password, result.Message);
    }

    [Fact]
    public async Task TheMockProvider_NeedsNoSecret_AndNoSettings()
    {
        var f = new Fixture();

        var result = await f.Service.TestAsync(new TestEmailCommand("me@example.com", ProviderType: ProviderType.Mock));

        Assert.True(result.Success);
        Assert.Single(f.Mock.Tested);
    }

    [Fact]
    public async Task SendGrid_NeedsAnApiKey_InTheRequest_AndAStoredOneIsNotUsedInItsPlace()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.SendGrid, RuntimeConfig.SendGrid, "SG-stored-key-1234567890");
        await f.Configs.Manager.ActivateAsync(saved.Id);

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(
            new TestEmailCommand("me@example.com", ProviderType: ProviderType.SendGrid, SettingsJson: RuntimeConfig.SendGrid)));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.Empty(f.SendGrid.Tested);   // the stored key was not borrowed
        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(
            new TestEmailCommand("me@example.com", ProviderType: ProviderType.SendGrid, SettingsJson: RuntimeConfig.SendGrid, Secret: "••••••••")));   // the mask is not a key
    }

    [Fact]
    public async Task IncompleteOrWrongSettings_AreRefused_BeforeAnythingIsTried()
    {
        var f = new Fixture();

        var incomplete = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(
            new TestEmailCommand("me@example.com", ProviderType: ProviderType.Smtp, SettingsJson: """{"port":25}""")));
        var withSecretInside = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(
            new TestEmailCommand("me@example.com", ProviderType: ProviderType.Smtp, SettingsJson: """{"host":"203.0.113.10","senderEmail":"a@b.co","password":"oops"}""")));
        var wrongChannel = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(
            new TestEmailCommand("me@example.com", ProviderType: ProviderType.S3, SettingsJson: "{}")));

        Assert.Contains("SettingsJson", incomplete.Errors.Keys);
        Assert.Contains("SettingsJson", withSecretInside.Errors.Keys);
        Assert.Contains("ProviderType", wrongChannel.Errors.Keys);
        Assert.Empty(f.Smtp.Tested);
    }

    [Fact]
    public async Task ABadRecipient_OrNothingToTest_IsRefused()
    {
        var f = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestEmailCommand("nope", ProviderType: ProviderType.Mock)));
        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestEmailCommand("me@example.com")));
    }

    // ------------------------------------------------------------------------------------------------ a saved version

    [Fact]
    public async Task ASavedVersion_IsTestedWithItsStoredSecret_AndTheResultIsRecorded()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.Smtp, RuntimeConfig.Smtp, Password);   // a draft: it can be tested before it is activated
        f.Configs.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await f.Service.TestAsync(new TestEmailCommand("me@example.com", ConfigurationId: saved.Id));

        Assert.True(result.Success);
        Assert.True(result.UsedSavedConfiguration);
        Assert.Equal(saved.Id, result.ConfigurationId);
        Assert.Equal(Password, Assert.Single(f.Smtp.Tested).Config.Secret);
        var row = f.Configs.Repository.Rows.Single();
        Assert.Equal((TestStatus.Success, f.Configs.Clock.GetUtcNow()), (row.LastTestStatus, row.LastTestedAt));
        Assert.Equal(ConfigurationStatus.Draft, row.Status);   // testing does not activate
    }

    [Fact]
    public async Task AFailedSavedTest_IsRecordedAsFailed()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.Smtp, RuntimeConfig.Smtp, Password);
        f.Smtp.TestResult = new EmailTestResult(false, "refused", 9);

        var result = await f.Service.TestAsync(new TestEmailCommand("me@example.com", ConfigurationId: saved.Id));

        Assert.False(result.Success);
        Assert.Equal(TestStatus.Failed, f.Configs.Repository.Rows.Single().LastTestStatus);
        Assert.Contains(f.Configs.Audit.Entries, e => e.Action == "SendTest" && e.DetailsJson.Contains("Failed") && !e.DetailsJson.Contains(Password));
    }

    [Fact]
    public async Task ASavedTest_IgnoresAnySettingsInTheSameRequest()
    {
        // The stored secret must never be used with settings from the request (it could be sent to a server of the caller's choice).
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.Smtp, RuntimeConfig.Smtp, Password);

        await f.Service.TestAsync(new TestEmailCommand("me@example.com", ConfigurationId: saved.Id,
            ProviderType: ProviderType.Smtp, SettingsJson: """{"host":"203.0.113.99","senderEmail":"evil@x.co"}""", Secret: "irrelevant"));

        var (config, _) = Assert.Single(f.Smtp.Tested);
        Assert.Equal("203.0.113.10", config.Settings.GetProperty("host").GetString());   // the saved host, not the requested one
        Assert.Equal(Password, config.Secret);
    }

    [Fact]
    public async Task AnUnknownId_IsNotFound_AndANonEmailConfigurationIsRefused()
    {
        var f = new Fixture();
        var telegram = await f.Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("default", ChannelType.Telegram, ProviderType.TelegramBot, "Bot", "{}", "123:token"));

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.TestAsync(new TestEmailCommand("me@example.com", ConfigurationId: Guid.NewGuid())));
        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestEmailCommand("me@example.com", ConfigurationId: telegram.Id)));
    }

    [Fact]
    public async Task AProviderThatIsNotAvailable_IsAFailedTest()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.Brevo, RuntimeConfig.SendGrid, "api-key-1234567890");

        var result = await f.Service.TestAsync(new TestEmailCommand("me@example.com", ConfigurationId: saved.Id));

        Assert.False(result.Success);
        Assert.Contains("Brevo", result.Message);
    }
}
