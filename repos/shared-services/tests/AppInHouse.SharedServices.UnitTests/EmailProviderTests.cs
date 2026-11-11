using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Email;
using AppInHouse.SharedServices.UnitTests.Support;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AppInHouse.SharedServices.UnitTests;

public class MockEmailProviderTests
{
    private static readonly ChannelRuntimeConfigurationFactory Config = new();

    private sealed class ChannelRuntimeConfigurationFactory
    {
        public Application.Configurations.ChannelRuntimeConfiguration Mock => RuntimeConfig.Make(ProviderType.Mock, "{}", null);
    }

    [Fact]
    public async Task ItRecordsWhatWasSent_InOrder()
    {
        var provider = new MockEmailProvider(NullLogger<MockEmailProvider>.Instance);

        var first = await provider.SendAsync(new EmailMessage("a@b.co", "One", "<p>1</p>", "1"), Config.Mock);
        await provider.SendAsync(new EmailMessage("c@d.co", "Two", "<p>2</p>"), Config.Mock);

        Assert.True(first.Success);
        Assert.StartsWith("mock-", first.ProviderMessageId);
        Assert.Equal(["One", "Two"], provider.SentMessages.Select(m => m.Subject));
        Assert.Equal("a@b.co", provider.SentMessages[0].To);
        Assert.Equal("<p>1</p>", provider.SentMessages[0].HtmlBody);
        Assert.Equal("1", provider.SentMessages[0].TextBody);
    }

    [Fact]
    public async Task TestConnection_Succeeds_AndRecordsTheTestEmail()
    {
        var provider = new MockEmailProvider(NullLogger<MockEmailProvider>.Instance);

        var result = await provider.TestConnectionAsync(Config.Mock, "me@example.com");

        Assert.True(result.Success);
        var sent = Assert.Single(provider.SentMessages);
        Assert.Equal("me@example.com", sent.To);
        Assert.Contains("Email test", sent.Subject);
    }

    [Fact]
    public async Task ItKeepsOnlyTheLastThousand_AndCanBeCleared()
    {
        var provider = new MockEmailProvider(NullLogger<MockEmailProvider>.Instance);
        for (var i = 0; i < 1005; i++) await provider.SendAsync(new EmailMessage("a@b.co", $"S{i}", "x"), Config.Mock);

        Assert.Equal(1000, provider.SentMessages.Count);
        Assert.Equal("S5", provider.SentMessages[0].Subject);   // the oldest five were dropped

        provider.Clear();
        Assert.Empty(provider.SentMessages);
    }

    [Fact]
    public async Task ItNeedsNoSecretAndNoSettings()
    {
        var provider = new MockEmailProvider(NullLogger<MockEmailProvider>.Instance);

        Assert.True((await provider.SendAsync(new EmailMessage("a@b.co", "s", "b"), RuntimeConfig.Make(ProviderType.Mock, "{}", secret: null))).Success);
    }
}

public class SendGridEmailProviderTests
{
    private const string ApiKey = "SG-fake-api-key-0123456789-abcdef";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static (SendGridEmailProvider Provider, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new SendGridEmailProvider(new HttpClient(handler) { BaseAddress = new Uri("https://api.sendgrid.test/") }, NullLogger<SendGridEmailProvider>.Instance), handler);
    }

    private static HttpResponseMessage Accepted() =>
        new(HttpStatusCode.Accepted) { Headers = { { "X-Message-Id", "sg-123" } } };

    private static readonly EmailMessage Message = new("to@example.com", "Subject here", "<p>Hello</p>", "Hello");

    [Fact]
    public async Task ItPostsTheMailToTheV3Endpoint_WithTheDecryptedKeyAsBearerToken()
    {
        var (provider, handler) = Make(_ => Accepted());

        var result = await provider.SendAsync(Message, RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, ApiKey));

        Assert.True(result.Success);
        Assert.Equal("sg-123", result.ProviderMessageId);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.sendgrid.test/v3/mail/send", request.RequestUri!.ToString());
        Assert.Equal(("Bearer", ApiKey), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.DoesNotContain(ApiKey, body);   // the key goes in the header, never in the payload
    }

    [Fact]
    public async Task ThePayload_HasTheShapeSendGridExpects()
    {
        var (provider, handler) = Make(_ => Accepted());

        await provider.SendAsync(Message, RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, ApiKey));

        using var payload = JsonDocument.Parse(handler.Calls[0].Body);
        var root = payload.RootElement;
        Assert.Equal("to@example.com", root.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("noreply@example.com", root.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("AppInHouse", root.GetProperty("from").GetProperty("name").GetString());
        Assert.Equal("Subject here", root.GetProperty("subject").GetString());
        var content = root.GetProperty("content");
        Assert.Equal(["text/plain", "text/html"], content.EnumerateArray().Select(c => c.GetProperty("type").GetString()));   // SendGrid wants text first
        Assert.Equal("<p>Hello</p>", content[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task ABodyWithoutText_SendsOnlyHtml()
    {
        var (provider, handler) = Make(_ => Accepted());

        await provider.SendAsync(new EmailMessage("to@example.com", "s", "<p>x</p>"), RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, ApiKey));

        using var payload = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.Equal(["text/html"], payload.RootElement.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task WithoutAKey_NothingIsSent()
    {
        var (provider, handler) = Make(_ => Accepted());

        var result = await provider.SendAsync(Message, RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, secret: null));

        Assert.False(result.Success);
        Assert.Contains("API key", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task IncompleteSettings_AreAResult_NotACall()
    {
        var (provider, handler) = Make(_ => Accepted());

        var result = await provider.SendAsync(Message, RuntimeConfig.Make(ProviderType.SendGrid, "{}", ApiKey));

        Assert.False(result.Success);
        Assert.Contains("senderEmail", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ARejectedKey_IsReportedWithoutRepeatingIt()
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent($$"""{"errors":[{"message":"The provided authorization grant is invalid: {{ApiKey}}"}]}""") });

        var result = await provider.SendAsync(Message, RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, ApiKey));

        Assert.False(result.Success);
        Assert.Contains("rejected the API key", result.Error);
        Assert.DoesNotContain(ApiKey, result.Error);
    }

    [Fact]
    public async Task AnErrorFromSendGrid_IsShortenedAndCleaned_EvenIfItEchoesTheKey()
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($$"""{"errors":[{"message":"Bad token {{ApiKey}}\nand more"}]}""") });

        var result = await provider.SendAsync(Message, RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, ApiKey));

        Assert.False(result.Success);
        Assert.StartsWith("SendGrid answered 400", result.Error);
        Assert.DoesNotContain(ApiKey, result.Error);
        Assert.DoesNotContain('\n', result.Error!);
    }

    [Fact]
    public async Task ANon_JsonErrorPage_StillGivesAShortMessage()
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") });

        var result = await provider.SendAsync(Message, RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, ApiKey));

        Assert.Equal("SendGrid answered 502.", result.Error);
    }

    [Fact]
    public async Task ANetworkFailure_IsAResult()
    {
        var (provider, _) = Make(_ => throw new HttpRequestException($"connection refused for key {ApiKey}"));

        var result = await provider.SendAsync(Message, RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, ApiKey));

        Assert.False(result.Success);
        Assert.Equal("Could not reach SendGrid.", result.Error);
    }

    [Fact]
    public async Task TestConnection_SendsATestMailToTheRecipient_AndReportsTheOutcome()
    {
        var (provider, handler) = Make(_ => Accepted());
        var config = RuntimeConfig.Make(ProviderType.SendGrid, RuntimeConfig.SendGrid, ApiKey);

        var ok = await provider.TestConnectionAsync(config, "me@example.com");

        Assert.True(ok.Success);
        Assert.Contains("me@example.com", handler.Calls[0].Body);
        Assert.DoesNotContain(ApiKey, handler.Calls[0].Body);

        var (failing, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var failed = await failing.TestConnectionAsync(config, "me@example.com");
        Assert.False(failed.Success);
        Assert.DoesNotContain(ApiKey, failed.Message);
    }
}

public class MailKitSmtpEmailProviderTests
{
    private const string Password = "smtp-P@ssw0rd-9876";

    private sealed class FakeSender : ISmtpSender
    {
        public List<(SmtpEndpoint Endpoint, MimeMessage Message)> Sent { get; } = [];
        public Func<CancellationToken, Task>? Behaviour { get; set; }

        public async Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken)
        {
            Sent.Add((endpoint, message));
            if (Behaviour is not null) await Behaviour(cancellationToken);
        }
    }

    private static MailKitSmtpEmailProvider Make(FakeSender sender, bool allowPrivate = true, int timeoutSeconds = 5)
    {
        var options = Options.Create(new EmailOptions { AllowPrivateNetworkHosts = allowPrivate, SmtpTimeoutSeconds = timeoutSeconds });
        return new MailKitSmtpEmailProvider(sender, new OutboundHostPolicy(options), options, NullLogger<MailKitSmtpEmailProvider>.Instance);
    }

    private static readonly EmailMessage Message = new("to@example.com", "Chào bạn", "<p>Nội dung <b>HTML</b></p>", "Nội dung văn bản");

    [Fact]
    public async Task ItConnectsWithTheConfiguredEndpoint_AndTheDecryptedPassword()
    {
        var sender = new FakeSender();

        var result = await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, Password));

        Assert.True(result.Success);
        var (endpoint, _) = Assert.Single(sender.Sent);
        Assert.Equal(("203.0.113.10", 2525, SecureSocketOptions.None, "mailer", Password),
            (endpoint.Host, endpoint.Port, endpoint.Security, endpoint.Username, endpoint.Password));
    }

    [Theory]
    [InlineData("None", SecureSocketOptions.None)]
    [InlineData("Auto", SecureSocketOptions.Auto)]
    [InlineData("SslOnConnect", SecureSocketOptions.SslOnConnect)]
    [InlineData("StartTls", SecureSocketOptions.StartTls)]
    public async Task TheSslSetting_SelectsTheSocketMode(string setting, SecureSocketOptions expected)
    {
        var sender = new FakeSender();
        var settings = $$"""{"host":"203.0.113.10","port":465,"useSsl":"{{setting}}","senderEmail":"noreply@example.com"}""";

        await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, settings, secret: null));

        Assert.Equal(expected, sender.Sent[0].Endpoint.Security);
    }

    [Fact]
    public async Task TheMimeMessage_HasTheSenderFromTheSettings_TheRecipient_TheSubjectAndBothBodies()
    {
        var sender = new FakeSender();

        await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, Password));

        var mime = sender.Sent[0].Message;
        Assert.Equal("noreply@example.com", Assert.IsType<MailboxAddress>(Assert.Single(mime.From)).Address);
        Assert.Equal("AppInHouse", ((MailboxAddress)mime.From[0]).Name);
        Assert.Equal("to@example.com", Assert.IsType<MailboxAddress>(Assert.Single(mime.To)).Address);
        Assert.Equal("Chào bạn", mime.Subject);
        Assert.Contains("HTML", mime.HtmlBody);
        Assert.Equal("Nội dung văn bản", mime.TextBody!.Trim());
        Assert.DoesNotContain(Password, mime.ToString());   // the password is for the login, never in the message
    }

    [Fact]
    public async Task ThePortDefaultsTo587_WhenNoneIsGiven()
    {
        var sender = new FakeSender();

        await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, """{"host":"203.0.113.10","senderEmail":"a@b.co"}""", secret: null));

        Assert.Equal(587, sender.Sent[0].Endpoint.Port);
    }

    [Fact]
    public async Task WithoutAUsername_ThereIsNoLogin()
    {
        var sender = new FakeSender();

        await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, """{"host":"203.0.113.10","port":25,"useSsl":"None","senderEmail":"a@b.co"}""", secret: null));

        Assert.Null(sender.Sent[0].Endpoint.Username);
    }

    [Fact]
    public async Task AUsernameWithoutAPassword_IsRefused_BeforeAnyConnection()
    {
        var sender = new FakeSender();

        var result = await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, secret: null));

        Assert.False(result.Success);
        Assert.Contains("password", result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task IncompleteSettings_AreAResult_NotAConnection()
    {
        var sender = new FakeSender();

        var result = await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, """{"port":25}""", secret: null));

        Assert.False(result.Success);
        Assert.Contains("host is required", result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ARejectedLogin_IsReportedWithoutThePassword()
    {
        var sender = new FakeSender { Behaviour = _ => throw new AuthenticationException($"535 5.7.8 bad credentials for {Password}") };

        var result = await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, Password));

        Assert.False(result.Success);
        Assert.Contains("rejected the login", result.Error);
        Assert.DoesNotContain(Password, result.Error);
    }

    [Fact]
    public async Task AConnectionFailure_NamesTheHostAndPort_ButNotTheExceptionText()
    {
        var sender = new FakeSender { Behaviour = _ => throw new SocketException((int)SocketError.ConnectionRefused) };

        var result = await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, Password));

        Assert.Equal("Could not connect to 203.0.113.10:2525.", result.Error);
    }

    [Fact]
    public async Task AnUnexpectedException_IsCleanedOfTheSecret()
    {
        var sender = new FakeSender { Behaviour = _ => throw new InvalidOperationException($"boom with {Password} inside") };

        var result = await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, Password));

        Assert.False(result.Success);
        Assert.DoesNotContain(Password, result.Error);
    }

    [Fact]
    public async Task ASlowServer_IsGivenUp_AtTheConfiguredTimeout()
    {
        var sender = new FakeSender { Behaviour = ct => Task.Delay(TimeSpan.FromSeconds(30), ct) };

        var result = await Make(sender, timeoutSeconds: 1).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, Password));

        Assert.False(result.Success);
        Assert.Contains("did not answer in time", result.Error);
    }

    [Fact]
    public async Task ACallerThatCancels_IsNotMistakenForATimeout()
    {
        var sender = new FakeSender { Behaviour = ct => Task.Delay(TimeSpan.FromSeconds(30), ct) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, Password), cancel.Token));
    }

    [Fact]
    public async Task TestConnection_SendsATestMail_AndReportsTheOutcome()
    {
        var sender = new FakeSender();
        var config = RuntimeConfig.Make(ProviderType.Smtp, RuntimeConfig.Smtp, Password);

        var ok = await Make(sender).TestConnectionAsync(config, "me@example.com");

        Assert.True(ok.Success);
        Assert.Equal("me@example.com", ((MailboxAddress)sender.Sent[0].Message.To[0]).Address);

        sender.Behaviour = _ => throw new AuthenticationException("no");
        var failed = await Make(sender).TestConnectionAsync(config, "me@example.com");
        Assert.False(failed.Success);
        Assert.Contains("rejected the login", failed.Message);
    }

    // ------------------------------------------------------------------------------------------------ which hosts may be contacted

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.20")]
    [InlineData("100.64.0.9")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public async Task PrivateAndLoopbackHosts_AreRefused_UnlessAllowed(string host)
    {
        var sender = new FakeSender();
        var settings = $$"""{"host":"{{host}}","senderEmail":"a@b.co"}""";

        var refused = await Make(sender, allowPrivate: false).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, settings, null));
        var allowed = await Make(sender, allowPrivate: true).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, settings, null));

        Assert.False(refused.Success);
        Assert.Contains("private or loopback", refused.Error);
        Assert.True(allowed.Success);
        Assert.Single(sender.Sent);   // only the allowed one connected
    }

    [Theory]
    [InlineData("169.254.169.254")]   // the cloud metadata address
    [InlineData("169.254.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("fe80::1")]
    public async Task LinkLocalMetadataAndMulticastHosts_AreAlwaysRefused(string host)
    {
        var sender = new FakeSender();
        var settings = $$"""{"host":"[{{host}}]","senderEmail":"a@b.co"}""".Replace("[" + host + "]", host);

        var result = await Make(sender, allowPrivate: true).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, settings, null));

        Assert.False(result.Success);
        Assert.Contains("not allowed", result.Error);
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]    // just outside 172.16.0.0/12
    [InlineData("2001:db8::1")]
    public async Task PublicHosts_AreFine_EvenWhenPrivateOnesAreNot(string host)
    {
        var sender = new FakeSender();

        var result = await Make(sender, allowPrivate: false).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, $$"""{"host":"{{host}}","senderEmail":"a@b.co"}""", null));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task AHostThatDoesNotResolve_IsAResult()
    {
        var sender = new FakeSender();

        var result = await Make(sender).SendAsync(Message, RuntimeConfig.Make(ProviderType.Smtp, """{"host":"no-such-host.invalid","senderEmail":"a@b.co"}""", null));

        Assert.False(result.Success);
        Assert.Contains("cannot be resolved", result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void TheSocketModes_MapOneToOne()
    {
        Assert.Equal(SecureSocketOptions.None, MailKitSmtpEmailProvider.ToSocketOptions(SmtpSecurity.None));
        Assert.Equal(SecureSocketOptions.StartTls, MailKitSmtpEmailProvider.ToSocketOptions(SmtpSecurity.StartTls));
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailKitSmtpEmailProvider.ToSocketOptions(SmtpSecurity.SslOnConnect));
        Assert.Equal(SecureSocketOptions.Auto, MailKitSmtpEmailProvider.ToSocketOptions(SmtpSecurity.Auto));
    }
}
