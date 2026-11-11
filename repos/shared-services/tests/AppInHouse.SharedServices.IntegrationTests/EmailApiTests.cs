using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.IntegrationTests;

public class EmailApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Configs = "/api/v1/channel-configurations";
    private const string Send = "/api/v1/emails/send";
    private const string Test = "/api/v1/emails/test";
    private const string Logs = "/api/v1/emails/delivery-logs";
    private const string StoredPassword = "Stored-SMTP-P@ssw0rd-111";
    private const string TypedPassword = "Typed-SMTP-P@ssw0rd-222";

    private static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];
    private static string NewRecipient() => $"user-{Guid.NewGuid():N}@example.com";

    private static object Smtp(FakeSmtpServer server, int? port = null) => new
    {
        host = "127.0.0.1", port = port ?? server.Port, useSsl = "None", username = "mailer",
        senderEmail = "noreply@example.com", senderName = "AppInHouse",
    };

    private async Task<HttpClient> AdminAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.EmailSend)).PlainKey);

    private async Task<HttpClient> ProjectClientAsync(string project, params string[] scopes) =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, scopes)).PlainKey);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    /// <summary>Saves a configuration through the API and (by default) activates it. Returns what the API answered for the saved version.</summary>
    private static async Task<JsonElement> SaveAsync(HttpClient admin, string project, string provider, object settings, string? secret = null, string name = "Email", bool activate = true)
    {
        var response = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Email", providerType = provider, name, settings, secret });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await ReadAsync(response);
        if (activate) Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Configs}/{saved.GetProperty("id").GetGuid()}/activate", null)).StatusCode);
        return saved;
    }

    private static object Verification(string to, string? project = null) => new
    {
        to, projectCode = project, template = "EmailVerification",
        variables = new Dictionary<string, string> { ["name"] = "Lan <b>", ["code"] = "482913" },
    };

    // ------------------------------------------------------------------------------------------------ sending

    [Fact]
    public async Task ASend_WithAMockConfiguration_DeliversAndWritesTheDeliveryLog()
    {
        var project = NewProject();
        var to = NewRecipient();
        await SaveAsync(await AdminAsync(), project, "Mock", new { senderEmail = "noreply@example.com" });
        var client = await ProjectClientAsync(project, ApiScopes.EmailSend, ApiScopes.ConfigRead);

        var response = await client.PostAsJsonAsync(Send, Verification(to));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("Sent", body.GetProperty("status").GetString());
        Assert.Equal(project, body.GetProperty("configurationProject").GetString());

        var mail = Assert.Single(factory.MockMail.SentMessages, m => m.To == to);
        Assert.Contains("482913", mail.HtmlBody);
        Assert.Contains("Lan &lt;b&gt;", mail.HtmlBody);   // the value was escaped, not injected

        var log = await factory.WithDbAsync(db => db.DeliveryLogs.SingleAsync(l => l.Recipient == to));
        Assert.Equal((project, ChannelType.Email, DeliveryStatus.Sent), (log.ProjectCode, log.ChannelType, log.Status));
        Assert.NotNull(log.LatencyMs);
        Assert.Equal(body.GetProperty("deliveryLogId").GetGuid(), log.Id);
    }

    [Fact]
    public async Task TheDeliveryLog_HoldsNoSubjectNoBodyAndNoCredential_OnTheWireOrInTheDatabase()
    {
        var project = NewProject();
        var to = NewRecipient();
        await SaveAsync(await AdminAsync(), project, "Mock", new { senderEmail = "noreply@example.com" });
        var client = await ProjectClientAsync(project, ApiScopes.EmailSend, ApiScopes.ConfigRead);
        await client.PostAsJsonAsync(Send, new { to, subject = "Very private subject 7781", htmlBody = "<p>Confidential body 7782</p>" });

        var listed = await (await client.GetAsync($"{Logs}?projectCode={project}")).Content.ReadAsStringAsync();
        var stored = await factory.WithDbAsync(db => db.Database
            .SqlQueryRaw<string>("SELECT recipient || '|' || status || '|' || coalesce(provider_response, '') AS \"Value\" FROM delivery_logs WHERE recipient = {0}", to).SingleAsync());

        foreach (var text in new[] { listed, stored })
        {
            Assert.DoesNotContain("private subject", text);
            Assert.DoesNotContain("Confidential", text);
        }

        Assert.Contains(to, listed);
    }

    [Fact]
    public async Task AProjectWithoutItsOwnConfiguration_UsesTheDefaultProjects()
    {
        var admin = await AdminAsync();
        await SaveAsync(admin, "default", "Mock", new { senderEmail = "noreply@example.com" });
        var project = NewProject();
        var client = await ProjectClientAsync(project, ApiScopes.EmailSend);

        var response = await client.PostAsJsonAsync(Send, Verification(NewRecipient()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("default", (await ReadAsync(response)).GetProperty("configurationProject").GetString());
    }

    [Fact]
    public async Task ASend_ToARealSmtpServer_LogsInWithTheStoredPassword_AndDeliversTheMessage()
    {
        await using var server = new FakeSmtpServer { Accept = ("mailer", StoredPassword) }.Start();
        var project = NewProject();
        var to = NewRecipient();
        await SaveAsync(await AdminAsync(), project, "Smtp", Smtp(server), StoredPassword);
        var client = await ProjectClientAsync(project, ApiScopes.EmailSend);

        var response = await client.PostAsJsonAsync(Send, Verification(to));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("mailer", StoredPassword), Assert.Single(server.Logins));   // decrypted from the key ring and used for the login
        var mail = Assert.Single(server.Mails);
        Assert.Equal((to, "noreply@example.com"), (mail.To, mail.From));
        Assert.Contains("Subject:", mail.Data);
        Assert.Contains("482913", mail.Data);
        Assert.DoesNotContain(StoredPassword, mail.Data);
        var log = await factory.WithDbAsync(db => db.DeliveryLogs.SingleAsync(l => l.Recipient == to));
        Assert.Equal(DeliveryStatus.Sent, log.Status);
        Assert.Contains("accepted", log.ProviderResponse);
    }

    [Fact]
    public async Task ARefusedLogin_Is502_WithTheReason_AndWithoutThePassword()
    {
        await using var server = new FakeSmtpServer { Accept = ("mailer", "the-right-password") }.Start();
        var project = NewProject();
        var to = NewRecipient();
        await SaveAsync(await AdminAsync(), project, "Smtp", Smtp(server), StoredPassword);
        var client = await ProjectClientAsync(project, ApiScopes.EmailSend);

        var response = await client.PostAsJsonAsync(Send, Verification(to));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("rejected the login", text);
        Assert.DoesNotContain(StoredPassword, text);
        Assert.DoesNotContain("the-right-password", text);
        Assert.Empty(server.Mails);
        var log = await factory.WithDbAsync(db => db.DeliveryLogs.SingleAsync(l => l.Recipient == to));
        Assert.Equal(DeliveryStatus.Failed, log.Status);
        Assert.DoesNotContain(StoredPassword, log.ProviderResponse);
    }

    [Fact]
    public async Task AServerThatIsNotThere_Is502_NamingTheHostAndPort()
    {
        int closedPort;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        var project = NewProject();
        await using var unused = new FakeSmtpServer().Start();
        await SaveAsync(await AdminAsync(), project, "Smtp", Smtp(unused, closedPort), StoredPassword);

        var response = await (await ProjectClientAsync(project, ApiScopes.EmailSend)).PostAsJsonAsync(Send, Verification(NewRecipient()));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains($"Could not connect to 127.0.0.1:{closedPort}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASend_ThroughSendGrid_UsesTheStoredKeyAsTheBearerToken()
    {
        const string key = "SG-integration-api-key-0001";
        var project = NewProject();
        var to = NewRecipient();
        await SaveAsync(await AdminAsync(), project, "SendGrid", new { senderEmail = "noreply@example.com", senderName = "AppInHouse" }, key);
        var client = await ProjectClientAsync(project, ApiScopes.EmailSend);
        factory.SendGrid.Calls.Clear();

        var response = await client.PostAsJsonAsync(Send, Verification(to));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (request, body) = Assert.Single(factory.SendGrid.Calls);
        Assert.Equal("/v3/mail/send", request.RequestUri!.AbsolutePath);
        Assert.Equal(key, request.Headers.Authorization!.Parameter);
        Assert.Contains(to, body);
        Assert.DoesNotContain(key, body);
        Assert.Contains("sg-integration-1", (await factory.WithDbAsync(db => db.DeliveryLogs.SingleAsync(l => l.Recipient == to))).ProviderResponse);
    }

    [Fact]
    public async Task ARejectedSendGridKey_Is502_AndTheKeyIsNotInTheAnswer()
    {
        const string key = "SG-integration-api-key-0002";
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "SendGrid", new { senderEmail = "noreply@example.com" }, key);
        factory.SendGrid.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent($$"""{"errors":[{"message":"invalid key {{key}}"}]}""") };
        try
        {
            var response = await (await ProjectClientAsync(project, ApiScopes.EmailSend)).PostAsJsonAsync(Send, Verification(NewRecipient()));
            var text = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Contains("rejected the API key", text);
            Assert.DoesNotContain(key, text);
        }
        finally
        {
            factory.SendGrid.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { { "X-Message-Id", "sg-integration-1" } } };
        }
    }

    [Fact]
    public async Task TheDeliveryLogsEndpoint_ListsTheNewestFirst_AndHonoursTheLimit()
    {
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "Mock", new { senderEmail = "noreply@example.com" });
        var client = await ProjectClientAsync(project, ApiScopes.EmailSend, ApiScopes.ConfigRead);
        var recipients = Enumerable.Range(0, 3).Select(_ => NewRecipient()).ToList();
        foreach (var to in recipients)
        {
            await client.PostAsJsonAsync(Send, Verification(to));
            await Task.Delay(15);   // distinct times, so "newest first" means something
        }

        var all = await ReadAsync(await client.GetAsync($"{Logs}?projectCode={project}"));
        var two = await ReadAsync(await client.GetAsync($"{Logs}?projectCode={project}&limit=2"));

        Assert.Equal(Enumerable.Reverse(recipients), all.EnumerateArray().Select(l => l.GetProperty("recipient").GetString()));
        Assert.Equal(Enumerable.Reverse(recipients).Take(2), two.EnumerateArray().Select(l => l.GetProperty("recipient").GetString()));
        Assert.All(all.EnumerateArray(), l => Assert.Equal("Sent", l.GetProperty("status").GetString()));
    }

    // ------------------------------------------------------------------------------------------------ who may do what

    [Fact]
    public async Task SendingNeedsTheEmailSendScope_AndAKey()
    {
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "Mock", new { });
        var onlyConfig = await ProjectClientAsync(project, ApiScopes.ConfigRead, ApiScopes.ConfigWrite);

        Assert.Equal(HttpStatusCode.Forbidden, (await onlyConfig.PostAsJsonAsync(Send, Verification(NewRecipient()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(Send, Verification(NewRecipient()))).StatusCode);
    }

    [Fact]
    public async Task AKey_CanOnlySendAndReadForItsOwnProject_ButThePlatformKeyCanActForAny()
    {
        var mine = NewProject();
        var other = NewProject();
        var admin = await AdminAsync();
        await SaveAsync(admin, mine, "Mock", new { });
        await SaveAsync(admin, other, "Mock", new { });
        var client = await ProjectClientAsync(mine, ApiScopes.EmailSend, ApiScopes.ConfigRead);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Send, Verification(NewRecipient(), mine))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Send, Verification(NewRecipient(), other))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Logs}?projectCode={mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Logs}?projectCode={other}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(Send, Verification(NewRecipient(), other))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Logs}?projectCode={other}")).StatusCode);

        // A key that names no project sees its own deliveries only.
        var own = await ReadAsync(await client.GetAsync(Logs));
        Assert.All(own.EnumerateArray(), l => Assert.Equal(mine, l.GetProperty("projectCode").GetString()));
    }

    [Fact]
    public async Task TheSendTestEndpoint_NeedsTheConfigWriteScope()
    {
        var project = NewProject();
        var sendOnly = await ProjectClientAsync(project, ApiScopes.EmailSend);

        Assert.Equal(HttpStatusCode.Forbidden, (await sendOnly.PostAsJsonAsync(Test, new { testRecipient = "me@example.com", providerType = "Mock" })).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ bad requests

    [Theory]
    [InlineData("not-an-email", "To")]
    [InlineData("a@b.co\r\nBcc: attacker@evil.test", "To")]
    public async Task ABadRecipient_Is400_AndNothingIsSent(string to, string field)
    {
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "Mock", new { });
        var before = factory.MockMail.SentMessages.Count;

        var response = await (await ProjectClientAsync(project, ApiScopes.EmailSend)).PostAsJsonAsync(Send, Verification(to));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(before, factory.MockMail.SentMessages.Count);
    }

    [Fact]
    public async Task ABadTemplateRequest_Is400_WithTheFieldToFix()
    {
        var project = NewProject();
        await SaveAsync(await AdminAsync(), project, "Mock", new { });
        var client = await ProjectClientAsync(project, ApiScopes.EmailSend);
        var to = NewRecipient();

        async Task<JsonElement> Errors(object body)
        {
            var response = await client.PostAsJsonAsync(Send, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await ReadAsync(response)).GetProperty("errors");
        }

        Assert.True((await Errors(new { to, template = "Nope" })).TryGetProperty("Template", out _));
        Assert.True((await Errors(new { to, template = "EmailVerification", variables = new { name = "Lan" } })).TryGetProperty("Variables", out _));
        Assert.True((await Errors(new { to, template = "PasswordReset", variables = new { name = "Lan", link = "javascript:alert(1)" } })).TryGetProperty("Variables", out _));
        Assert.True((await Errors(new { to, template = "EmailVerification", subject = "also custom" })).TryGetProperty("Template", out _));
        Assert.True((await Errors(new { to })).TryGetProperty("Template", out _));
    }

    // ------------------------------------------------------------------------------------------------ Send Test (P0)

    [Fact]
    public async Task SendTest_WithAnUnsavedConfiguration_SendsForReal_AndStoresAndChangesNothing()
    {
        await using var server = new FakeSmtpServer { Accept = ("mailer", TypedPassword) }.Start();
        var project = NewProject();
        var admin = await AdminAsync();
        // The saved, active configuration uses another password (and so would not work on this server): the test must use the typed one.
        var active = await SaveAsync(admin, project, "Smtp", Smtp(server), StoredPassword);
        var activeId = active.GetProperty("id").GetGuid();
        var before = await factory.WithDbAsync(db => db.ChannelConfigurations.AsNoTracking().OrderBy(c => c.Id).ToListAsync());
        var to = NewRecipient();

        var response = await admin.PostAsJsonAsync(Test, new { testRecipient = to, providerType = "Smtp", settings = Smtp(server), secret = TypedPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.False(body.GetProperty("usedSavedConfiguration").GetBoolean());
        Assert.Equal(("mailer", TypedPassword), Assert.Single(server.Logins));
        Assert.Equal(to, Assert.Single(server.Mails).To);

        // The point of the feature: nothing in channel_configurations was created or changed.
        var after = await factory.WithDbAsync(db => db.ChannelConfigurations.AsNoTracking().OrderBy(c => c.Id).ToListAsync());
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(c => (c.Id, c.Version, c.Status, c.SettingsJson, c.EncryptedSecret, c.LastTestedAt, c.LastTestStatus, c.UpdatedAt)),
                     after.Select(c => (c.Id, c.Version, c.Status, c.SettingsJson, c.EncryptedSecret, c.LastTestedAt, c.LastTestStatus, c.UpdatedAt)));
        var stillActive = Assert.Single(after, c => c.ProjectCode == project && c.Status == ConfigurationStatus.Active);
        Assert.Equal(activeId, stillActive.Id);
        Assert.DoesNotContain(TypedPassword, JsonSerializer.Serialize(after.Select(c => (c.SettingsJson, c.EncryptedSecret))));

        // Only an audit entry says who pressed the button, and it holds no secret.
        var audit = await factory.WithDbAsync(db => db.AuditLogs.Where(a => a.Action == "SendTest" && a.ResourceId == "unsaved").ToListAsync());
        Assert.NotEmpty(audit);
        Assert.All(audit, a => Assert.DoesNotContain(TypedPassword, a.DetailsJson));
    }

    [Fact]
    public async Task SendTest_WithAMockConfiguration_NeedsNoSecret_AndCreatesNothing()
    {
        var admin = await AdminAsync();
        var rows = await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync());
        var to = NewRecipient();

        var response = await admin.PostAsJsonAsync(Test, new { testRecipient = to, providerType = "Mock", settings = new { } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("success").GetBoolean());
        Assert.Contains(factory.MockMail.SentMessages, m => m.To == to);
        Assert.Equal(rows, await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync()));
    }

    [Fact]
    public async Task SendTest_OfASavedVersion_UsesItsStoredPassword_AndRecordsTheResult()
    {
        await using var server = new FakeSmtpServer { Accept = ("mailer", StoredPassword) }.Start();
        var project = NewProject();
        var admin = await AdminAsync();
        var draft = await SaveAsync(admin, project, "Smtp", Smtp(server), StoredPassword, activate: false);   // a draft can be tested before it is activated
        var id = draft.GetProperty("id").GetGuid();

        var response = await admin.PostAsJsonAsync(Test, new { testRecipient = NewRecipient(), configurationId = id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.True(body.GetProperty("usedSavedConfiguration").GetBoolean());
        Assert.Equal(id, body.GetProperty("configurationId").GetGuid());
        Assert.Equal(("mailer", StoredPassword), Assert.Single(server.Logins));
        var row = await factory.WithDbAsync(db => db.ChannelConfigurations.AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.Equal(TestStatus.Success, row.LastTestStatus);
        Assert.NotNull(row.LastTestedAt);
        Assert.Equal(ConfigurationStatus.Draft, row.Status);   // testing did not activate it

        var fetched = await ReadAsync(await admin.GetAsync($"{Configs}/{id}"));
        Assert.Equal("Success", fetched.GetProperty("lastTestStatus").GetString());
    }

    [Fact]
    public async Task SendTest_OfASavedVersion_ThatFails_IsRecordedAsFailed_WithoutExposingThePassword()
    {
        await using var server = new FakeSmtpServer { Accept = ("mailer", "not-the-stored-one") }.Start();
        var project = NewProject();
        var admin = await AdminAsync();
        var saved = await SaveAsync(admin, project, "Smtp", Smtp(server), StoredPassword, activate: false);
        var id = saved.GetProperty("id").GetGuid();

        var response = await admin.PostAsJsonAsync(Test, new { testRecipient = NewRecipient(), configurationId = id });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(JsonDocument.Parse(text).RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("rejected the login", text);
        Assert.DoesNotContain(StoredPassword, text);
        Assert.Equal(TestStatus.Failed, (await factory.WithDbAsync(db => db.ChannelConfigurations.AsNoTracking().SingleAsync(c => c.Id == id))).LastTestStatus);
    }

    [Fact]
    public async Task SendTest_ThatFailsFromMemory_ShowsASafeShortMessage()
    {
        await using var server = new FakeSmtpServer { Accept = ("mailer", "something-else") }.Start();
        var admin = await AdminAsync();

        var response = await admin.PostAsJsonAsync(Test, new { testRecipient = NewRecipient(), providerType = "Smtp", settings = Smtp(server), secret = TypedPassword });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.True(body.GetProperty("message").GetString()!.Length <= 220);
        Assert.DoesNotContain(TypedPassword, text);
    }

    [Fact]
    public async Task SendTest_ToAServerThatIsNotThere_IsAFailedTest_NotAnError()
    {
        int closedPort;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        await using var unused = new FakeSmtpServer().Start();
        var response = await (await AdminAsync()).PostAsJsonAsync(Test, new { testRecipient = NewRecipient(), providerType = "Smtp", settings = Smtp(unused, closedPort), secret = TypedPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("Could not connect", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SendTest_OfSendGridFromMemory_UsesTheTypedKey_AndAFailureDoesNotEchoIt()
    {
        const string key = "SG-typed-api-key-0003";
        var admin = await AdminAsync();
        factory.SendGrid.Calls.Clear();

        var ok = await ReadAsync(await admin.PostAsJsonAsync(Test, new { testRecipient = NewRecipient(), providerType = "SendGrid", settings = new { senderEmail = "noreply@example.com" }, secret = key }));
        Assert.True(ok.GetProperty("success").GetBoolean());
        Assert.Equal(key, Assert.Single(factory.SendGrid.Calls).Request.Headers.Authorization!.Parameter);

        factory.SendGrid.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent($$"""{"errors":[{"message":"bad {{key}}"}]}""") };
        try
        {
            var response = await admin.PostAsJsonAsync(Test, new { testRecipient = NewRecipient(), providerType = "SendGrid", settings = new { senderEmail = "noreply@example.com" }, secret = key });
            var text = await response.Content.ReadAsStringAsync();
            Assert.False(JsonDocument.Parse(text).RootElement.GetProperty("success").GetBoolean());
            Assert.DoesNotContain(key, text);
        }
        finally
        {
            factory.SendGrid.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { { "X-Message-Id", "sg-integration-1" } } };
        }
    }

    [Fact]
    public async Task SendTest_RefusesWhatCannotBeTried()
    {
        var admin = await AdminAsync();
        async Task<JsonElement> Errors(object body)
        {
            var response = await admin.PostAsJsonAsync(Test, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await ReadAsync(response)).GetProperty("errors");
        }

        Assert.True((await Errors(new { testRecipient = "nope", providerType = "Mock" })).TryGetProperty("TestRecipient", out _));
        Assert.True((await Errors(new { testRecipient = "me@example.com", providerType = "SendGrid", settings = new { senderEmail = "a@b.co" } })).TryGetProperty("Secret", out _));   // no key, and none is borrowed
        Assert.True((await Errors(new { testRecipient = "me@example.com", providerType = "Smtp", settings = new { port = 25 } })).TryGetProperty("SettingsJson", out _));          // incomplete
        Assert.True((await Errors(new { testRecipient = "me@example.com", providerType = "Smtp", settings = new { host = "127.0.0.1", senderEmail = "a@b.co", password = "oops" } })).TryGetProperty("SettingsJson", out _));
        Assert.True((await Errors(new { testRecipient = "me@example.com", providerType = "S3", settings = new { } })).TryGetProperty("ProviderType", out _));
        Assert.True((await Errors(new { testRecipient = "me@example.com" })).TryGetProperty("ProviderType", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Test, new { testRecipient = "me@example.com", configurationId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task ATestOfSettingsThatAreNotSaved_NeverUsesTheStoredPassword()
    {
        // The stored password must not be sent to a server chosen in the request.
        await using var attacker = new FakeSmtpServer().Start();
        await using var real = new FakeSmtpServer().Start();
        var project = NewProject();
        var admin = await AdminAsync();
        var saved = await SaveAsync(admin, project, "Smtp", Smtp(real), StoredPassword);

        await admin.PostAsJsonAsync(Test, new { testRecipient = NewRecipient(), configurationId = saved.GetProperty("id").GetGuid(), providerType = "Smtp", settings = Smtp(attacker), secret = "ignored" });

        Assert.Empty(attacker.Logins);                         // nothing reached the server named in the request
        Assert.Equal(StoredPassword, Assert.Single(real.Logins).Password);   // the saved version was tested, on its own server
    }

    // ------------------------------------------------------------------------------------------------ changing the provider (review Minor #3)

    [Fact]
    public async Task ChangingTheProvider_NeedsANewSecret_ThroughTheApiToo()
    {
        await using var server = new FakeSmtpServer().Start();
        var project = NewProject();
        var admin = await AdminAsync();
        await SaveAsync(admin, project, "Smtp", Smtp(server), StoredPassword, activate: false);

        var withoutSecret = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Email", providerType = "SendGrid", name = "Email", settings = new { senderEmail = "a@b.co" } });
        var withSecret = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Email", providerType = "SendGrid", name = "Email", settings = new { senderEmail = "a@b.co" }, secret = "SG-new-api-key-0004" });
        var toMock = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Email", providerType = "Mock", name = "Email", settings = new { } });

        Assert.Equal(HttpStatusCode.BadRequest, withoutSecret.StatusCode);
        Assert.True((await ReadAsync(withoutSecret)).GetProperty("errors").TryGetProperty("Secret", out _));
        Assert.Equal(HttpStatusCode.Created, withSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Created, toMock.StatusCode);
        Assert.False((await ReadAsync(toMock)).GetProperty("hasSecret").GetBoolean());   // the old password did not follow it
    }

    [Fact]
    public async Task BadEmailSettings_AreRefusedWhenSaving()
    {
        var response = await (await AdminAsync()).PostAsJsonAsync(Configs, new
        {
            projectCode = NewProject(), channelType = "Email", providerType = "Smtp", name = "Email",
            settings = new { host = "smtp://host", port = 99999, senderEmail = "Name <a@b.co>" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("port", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>On its own host (and database): no email configuration exists anywhere.</summary>
public class EmailNoConfigurationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task WhenNeitherTheProjectNorDefaultHasOne_TheSendIs409_AndIsLoggedAsFailed()
    {
        var (key, _) = await factory.CreateKeyForProjectAsync("shop", null, ApiScopes.EmailSend, ApiScopes.ConfigRead);
        var client = factory.ClientWithKey(key);
        var to = $"user-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/api/v1/emails/send", new { to, template = "SystemNotification", variables = new { title = "T", message = "M" } });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("no active email configuration", text, StringComparison.OrdinalIgnoreCase);
        var log = await factory.WithDbAsync(db => db.DeliveryLogs.SingleAsync(l => l.Recipient == to));
        Assert.Equal((DeliveryStatus.Failed, "shop"), (log.Status, log.ProjectCode));
    }

    [Fact]
    public async Task ADraftIsNotEnough_OnlyAnActiveConfigurationSends()
    {
        var (adminKey, _) = await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite);
        var admin = factory.ClientWithKey(adminKey);
        await admin.PostAsJsonAsync("/api/v1/channel-configurations", new { projectCode = "drafty", channelType = "Email", providerType = "Mock", name = "Email", settings = new { } });
        var (key, _) = await factory.CreateKeyForProjectAsync("drafty", null, ApiScopes.EmailSend);

        var response = await factory.ClientWithKey(key).PostAsJsonAsync("/api/v1/emails/send", new { to = "a@b.co", subject = "s", textBody = "b" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}

/// <summary>Review finding (TASK-032, Minor #1): in Development an unreadable body used to come back as 500.</summary>
public class DevelopmentRequestErrorTests(DevelopmentApiFactory factory) : IClassFixture<DevelopmentApiFactory>
{
    private async Task<HttpClient> AdminAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.EmailSend)).PlainKey);

    [Theory]
    [InlineData("/api/v1/channel-configurations", """{"projectCode":"p","channelType":"Fax","providerType":"Smtp","name":"n"}""")]   // an unknown enum name
    [InlineData("/api/v1/channel-configurations", """{"projectCode":"p","channelType":"Em""")]                                         // JSON cut short
    [InlineData("/api/v1/channel-configurations", "this is not json")]
    [InlineData("/api/v1/emails/send", """{"to":"a@b.co","variables":"should-be-an-object"}""")]
    [InlineData("/api/v1/emails/send", """{"to":""")]
    [InlineData("/api/v1/emails/test", """{"testRecipient":"a@b.co","providerType":"Carrier pigeon"}""")]
    [InlineData("/api/v1/channel-configurations/rollback", """{"projectCode":"p","channelType":"Email","name":"n","targetVersion":"one"}""")]
    public async Task AnUnreadableBody_Is400ProblemDetails_NotA500_InDevelopment(string url, string body)
    {
        var client = await AdminAsync();

        var response = await client.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(400, JsonDocument.Parse(text).RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("Exception", text);          // no stack trace, no internals
        Assert.DoesNotContain("Newtonsoft", text);
    }

    [Fact]
    public async Task AMissingBody_Is400_Too()
    {
        var client = await AdminAsync();

        var response = await client.PostAsync("/api/v1/emails/send", content: null);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType, $"got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task ARequestThatIsFine_StillWorks_InDevelopment()
    {
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync("/api/v1/emails/test", new { testRecipient = "me@example.com", providerType = "Mock", settings = new { } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
