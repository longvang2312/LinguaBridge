using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;

namespace AppInHouse.SharedServices.UnitTests;

// Review finding (TASK-032, Minor #3): a new version with another provider must not inherit the previous provider's secret.

public class ProviderChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheDomain_DoesNotCarryASecretToAnotherProvider()
    {
        var smtp = ChannelConfiguration.CreateFirstVersion("p", ChannelType.Email, ProviderType.Smtp, "Email", "{}", "cipher-smtp", Now);
        var series = new List<ChannelConfiguration> { smtp };

        var sameProvider = ChannelConfigurationVersioning.CreateNextVersion(series, ProviderType.Smtp, "{}", null, Now);
        var otherProvider = ChannelConfigurationVersioning.CreateNextVersion(series, ProviderType.SendGrid, "{}", null, Now);
        var otherWithOwn = ChannelConfigurationVersioning.CreateNextVersion(series, ProviderType.SendGrid, "{}", "cipher-sg", Now);

        Assert.Equal("cipher-smtp", sameProvider.EncryptedSecret);   // same provider: inherited
        Assert.Null(otherProvider.EncryptedSecret);                  // another provider: never
        Assert.Equal("cipher-sg", otherWithOwn.EncryptedSecret);
    }

    [Fact]
    public void WhichProvidersNeedASecret()
    {
        Assert.True(ProviderCatalog.RequiresSecret(ProviderType.SendGrid));
        Assert.True(ProviderCatalog.RequiresSecret(ProviderType.Brevo));
        Assert.True(ProviderCatalog.RequiresSecret(ProviderType.TelegramBot));
        Assert.True(ProviderCatalog.RequiresSecret(ProviderType.S3));
        Assert.False(ProviderCatalog.RequiresSecret(ProviderType.Smtp));        // a relay may take mail without a login
        Assert.False(ProviderCatalog.RequiresSecret(ProviderType.Mock));
        Assert.False(ProviderCatalog.RequiresSecret(ProviderType.LocalDisk));
    }

    [Fact]
    public async Task SmtpToSendGrid_WithoutANewSecret_IsRefused_AndNothingIsSaved()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Email", """{"host":"smtp.example.com"}""", "smtp-password"));
        var rows = fixture.Repository.Rows.Count;
        var audits = fixture.Audit.Entries.Count;

        var error = await Assert.ThrowsAsync<ValidationException>(() => fixture.Manager.SaveAsync(
            new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", """{"senderEmail":"a@b.co"}""", Secret: null)));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.Contains("SendGrid", error.Errors["Secret"][0]);
        Assert.Contains("not reused", error.Errors["Secret"][0]);
        Assert.Equal(rows, fixture.Repository.Rows.Count);
        Assert.Equal(audits, fixture.Audit.Entries.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("••••••••")]   // the mask a form echoes back is not a new secret
    public async Task TheMask_AndABlank_AreNotANewSecret(string? secret)
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Email", "{}", "smtp-password"));

        await Assert.ThrowsAsync<ValidationException>(() => fixture.Manager.SaveAsync(
            new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", "{}", secret)));
    }

    [Fact]
    public async Task SmtpToSendGrid_WithANewSecret_IsAccepted_AndStoresOnlyTheNewSecret()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Smtp, "Email", "{}", "smtp-password"));

        var saved = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", """{"senderEmail":"a@b.co"}""", "SG-new-api-key-123456"));

        Assert.Equal((2, ProviderType.SendGrid), (saved.Version, saved.ProviderType));
        var row = fixture.Repository.Rows.Single(r => r.Version == 2);
        Assert.Equal("SG-new-api-key-123456", fixture.Protector.Unprotect(row.EncryptedSecret!));
    }

    [Fact]
    public async Task SendGridToMock_NeedsNoSecret_AndDoesNotKeepTheOldOne()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", "{}", "SG-old-api-key-123456"));

        var mock = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Mock, "Email", "{}", Secret: null));

        Assert.False(mock.HasSecret);   // a mock has no credential, and the SendGrid key did not follow it
        Assert.Null(fixture.Repository.Rows.Single(r => r.Version == 2).EncryptedSecret);
    }

    [Fact]
    public async Task BackToTheOldProvider_AfterAMock_NeedsTheSecretAgain_ForARequiredOne()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", "{}", "SG-key-1234567890"));
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Mock, "Email", "{}", null));

        await Assert.ThrowsAsync<ValidationException>(() => fixture.Manager.SaveAsync(
            new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", "{}", null)));
    }

    [Fact]
    public async Task TheSameProvider_StillKeepsTheSecret_WhenNoneIsTyped()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", """{"senderEmail":"a@b.co"}""", "SG-key-1234567890"));

        var next = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", """{"senderEmail":"other@b.co"}""", null));

        Assert.True(next.HasSecret);
        Assert.Equal("SG-key-1234567890", fixture.Protector.Unprotect(fixture.Repository.Rows.Single(r => r.Version == 2).EncryptedSecret!));
    }

    [Fact]
    public async Task ANewSeriesNeedsNoSecretToBeSavedAsADraft()
    {
        var fixture = new ManagerFixture();

        var draft = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.SendGrid, "Email", "{}", null));

        Assert.False(draft.HasSecret);   // a draft may be incomplete; sending and testing say what is missing
    }
}

public class EmailSettingsOnSaveTests
{
    private static SaveChannelConfigurationCommand Smtp(string settings) =>
        new("p", ChannelType.Email, ProviderType.Smtp, "Email", settings, null);

    [Theory]
    [InlineData("""{"port":99999}""")]
    [InlineData("""{"port":"25"}""")]
    [InlineData("""{"useSsl":"maybe"}""")]
    [InlineData("""{"host":"http://smtp.example.com"}""")]
    [InlineData("""{"senderEmail":"Name <a@b.co>"}""")]
    public void BadEmailSettings_AreRefusedOnSave(string settings)
    {
        var errors = ChannelConfigurationValidator.Validate(Smtp(settings));

        Assert.Contains(nameof(SaveChannelConfigurationCommand.SettingsJson), errors.Keys);
    }

    [Fact]
    public void GoodAndIncompleteEmailSettings_AreFineForADraft()
    {
        Assert.Empty(ChannelConfigurationValidator.Validate(Smtp("""{"host":"smtp.example.com","port":587,"useSsl":"StartTls","senderEmail":"a@b.co"}""")));
        Assert.Empty(ChannelConfigurationValidator.Validate(Smtp("{}")));
    }

    [Fact]
    public void TheEmailRulesDoNotApplyToOtherChannels()
    {
        // "port" means nothing to a Telegram bot, and is not judged as an email setting.
        var telegram = new SaveChannelConfigurationCommand("p", ChannelType.Telegram, ProviderType.TelegramBot, "Bot", """{"port":99999}""", null);

        Assert.Empty(ChannelConfigurationValidator.Validate(telegram));
    }
}

public class ManagerTestResultTests
{
    [Fact]
    public async Task RecordTestResult_UpdatesOnlyTheTestFields_AndAudits()
    {
        var fixture = new ManagerFixture();
        var saved = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand());
        var before = fixture.Repository.Rows.Single().SettingsJson;
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));

        await fixture.Manager.RecordTestResultAsync(saved.Id, TestStatus.Success);

        var row = fixture.Repository.Rows.Single();
        Assert.Equal((TestStatus.Success, fixture.Clock.GetUtcNow()), (row.LastTestStatus, row.LastTestedAt));
        Assert.Equal(before, row.SettingsJson);
        Assert.Equal(ConfigurationStatus.Draft, row.Status);
        Assert.Contains(fixture.Audit.Entries, e => e.Action == "SendTest" && e.ResourceId == saved.Id.ToString());
    }

    [Fact]
    public async Task RecordTestResult_OfAnUnknownId_IsNotFound()
    {
        var fixture = new ManagerFixture();

        await Assert.ThrowsAsync<NotFoundException>(() => fixture.Manager.RecordTestResultAsync(Guid.NewGuid(), TestStatus.Failed));
    }

    [Fact]
    public async Task GetForRuntime_ReturnsAnyVersion_WithItsSecret_AndNullForAnUnknownId()
    {
        var fixture = new ManagerFixture();
        var draft = await fixture.Manager.SaveAsync(ManagerFixture.SmtpCommand(secret: "runtime-secret"));

        var runtime = await fixture.Manager.GetForRuntimeAsync(draft.Id);

        Assert.Equal("runtime-secret", runtime!.Secret);
        Assert.Equal(ChannelType.Email, runtime.ChannelType);
        Assert.Null(await fixture.Manager.GetForRuntimeAsync(Guid.NewGuid()));
    }
}

public class EmailClientSdkTests
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
    public async Task SendEmail_PostsTheRequest_AndReadsTheAnswer()
    {
        var (client, handler) = Make(_ => Json(System.Net.HttpStatusCode.OK, """{"success":true,"status":"Sent","deliveryLogId":"7f2d6a1e-0000-0000-0000-000000000009","latencyMs":42,"configurationProject":"default","configurationVersion":3}"""));

        var response = await client.SendEmailAsync(new SendEmailRequest("user@example.com", Template: "EmailVerification",
            Variables: new Dictionary<string, string> { ["name"] = "Lan", ["code"] = "1" }));

        Assert.True(response.Success);
        Assert.Equal(("Sent", 42, "default", 3), (response.Status, response.LatencyMs, response.ConfigurationProject, response.ConfigurationVersion));
        Assert.Equal(("/api/v1/emails/send", HttpMethod.Post), (handler.Requests[0].Url, handler.Requests[0].Method));
        Assert.Contains("\"to\":\"user@example.com\"", handler.Requests[0].Body);
        Assert.Contains("\"template\":\"EmailVerification\"", handler.Requests[0].Body);
        Assert.Contains("\"code\":\"1\"", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Conflict, 409, "No email configuration")]
    [InlineData(System.Net.HttpStatusCode.BadGateway, 502, "could not be delivered")]
    public async Task SendEmail_ThatTheServiceRefuses_ThrowsWithTheStatusAndTheDetail(System.Net.HttpStatusCode status, int code, string title)
    {
        var (client, _) = Make(_ => Json(status, $$"""{"title":"{{title}}","detail":"Project shop has no active email configuration."}"""));

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.SendEmailAsync(new SendEmailRequest("a@b.co", Template: "PasswordReset")));

        Assert.Equal(code, exception.StatusCode);
        Assert.Contains("shop", exception.Message);
    }

    [Fact]
    public async Task SendTestEmail_PostsToTheTestEndpoint_WithTheSecretAsAWriteOnlyField()
    {
        var (client, handler) = Make(_ => Json(System.Net.HttpStatusCode.OK, """{"success":false,"message":"The SMTP server rejected the login.","latencyMs":80,"providerType":"Smtp","usedSavedConfiguration":false,"configurationId":null}"""));
        using var settings = System.Text.Json.JsonDocument.Parse("""{"host":"smtp.example.com"}""");

        var response = await client.SendTestEmailAsync(new SendTestEmailRequest("me@example.com", ProviderType: "Smtp", Settings: settings.RootElement.Clone(), Secret: "typed-password"));

        Assert.False(response.Success);
        Assert.Contains("rejected the login", response.Message);
        Assert.Equal("/api/v1/emails/test", handler.Requests[0].Url);
        Assert.Contains("\"secret\":\"typed-password\"", handler.Requests[0].Body);
        Assert.Contains("\"testRecipient\":\"me@example.com\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task GetEmailDeliveryLogs_BuildsTheQuery_AndReadsTheRows()
    {
        var (client, handler) = Make(_ => Json(System.Net.HttpStatusCode.OK, """[{"id":"7f2d6a1e-0000-0000-0000-000000000009","projectCode":"default","channelType":"Email","recipient":"a@b.co","status":"Sent","providerResponse":"250 OK","latencyMs":12,"createdAt":"2026-11-11T08:00:00+00:00"}]"""));

        var logs = await client.GetEmailDeliveryLogsAsync("default", 5);

        var log = Assert.Single(logs);
        Assert.Equal(("a@b.co", "Sent", 12), (log.Recipient, log.Status, log.LatencyMs));
        Assert.Equal("/api/v1/emails/delivery-logs?limit=5&projectCode=default", handler.Requests[0].Url);

        await client.GetEmailDeliveryLogsAsync();
        Assert.Equal("/api/v1/emails/delivery-logs?limit=20", handler.Requests[1].Url);
    }
}
