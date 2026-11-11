using System.Net;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Chat;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure;
using AppInHouse.SharedServices.Infrastructure.Chat;
using AppInHouse.SharedServices.Infrastructure.Email;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AppInHouse.SharedServices.UnitTests;

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
        return respond(request);
    }
}

public class MockChatProviderTests
{
    private static MockChatProvider Make() => new(NullLogger<MockChatProvider>.Instance);

    [Fact]
    public async Task ItRecordsWhatWasSent_InOrder()
    {
        var provider = Make();
        var config = RuntimeConfig.Make(ProviderType.Mock, "{}", null);

        var first = await provider.SendMessageAsync(new ChatMessage("-100", "one", ChatFormat.PlainText, "p"), config);
        await provider.SendMessageAsync(new ChatMessage(null, "two", ChatFormat.Markdown, "p"), config);

        Assert.True(first.Success);
        Assert.StartsWith("mock-", first.ProviderMessageId);
        Assert.Equal(["one", "two"], provider.SentMessages.Select(m => m.Text));
        Assert.Equal(("-100", ChatFormat.PlainText, "p"), (provider.SentMessages[0].Recipient, provider.SentMessages[0].Format, provider.SentMessages[0].ProjectCode));
        Assert.Null(provider.SentMessages[1].Recipient);
    }

    [Fact]
    public async Task TestConnection_RecordsTheTestMessage_ForTheRecipient()
    {
        var provider = Make();

        var result = await provider.TestConnectionAsync(RuntimeConfig.Make(ProviderType.Mock, "{}", null), "-200", "hello test");

        Assert.True(result.Success);
        var sent = Assert.Single(provider.SentMessages);
        Assert.Equal(("-200", "hello test"), (sent.Recipient, sent.Text));
    }

    [Fact]
    public async Task AnEmptyTestRecipient_MeansTheDefault()
    {
        var provider = Make();

        await provider.TestConnectionAsync(RuntimeConfig.Make(ProviderType.Mock, "{}", null), string.Empty, "t");

        Assert.Null(provider.SentMessages[0].Recipient);
    }

    [Fact]
    public async Task ItKeepsOnlyTheLastThousand_AndCanBeCleared()
    {
        var provider = Make();
        var config = RuntimeConfig.Make(ProviderType.Mock, "{}", null);
        for (var i = 0; i < 1005; i++) await provider.SendMessageAsync(new ChatMessage(null, $"m{i}", null, "p"), config);

        Assert.Equal(1000, provider.SentMessages.Count);
        Assert.Equal("m5", provider.SentMessages[0].Text);

        provider.Clear();
        Assert.Empty(provider.SentMessages);
    }
}

public class TelegramBotChatProviderTests
{
    private static readonly string Token = FakeChatSecrets.TelegramToken;
    private const string Settings = """{"defaultChatId":"-1001234567890","parseMode":"PlainText"}""";

    private static (TelegramBotChatProvider Provider, StubHttpHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new StubHttpHandler(respond ?? (_ => Ok()));
        return (new TelegramBotChatProvider(new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.test/") }, NullLogger<TelegramBotChatProvider>.Instance), handler);
    }

    private static HttpResponseMessage Ok(long id = 4242) =>
        new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":" + id + "}}") };

    private static HttpResponseMessage Failure(HttpStatusCode status, string description, string extra = "") =>
        new(status) { Content = new StringContent("{\"ok\":false,\"error_code\":" + (int)status + ",\"description\":\"" + description + "\"" + extra + "}") };

    private static ChannelRuntimeConfigurationHolder Config(string settings = Settings, string? token = null) => new(RuntimeConfig.Make(ProviderType.TelegramBot, settings, token ?? Token));

    private sealed record ChannelRuntimeConfigurationHolder(AppInHouse.SharedServices.Application.Configurations.ChannelRuntimeConfiguration Value);

    private static JsonElement Body(StubHttpHandler handler) => JsonDocument.Parse(handler.Calls.Last().Body).RootElement.Clone();

    [Fact]
    public async Task ItPostsToSendMessage_WithTheDecryptedTokenInTheAddress()
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hello", ChatFormat.PlainText, "p"), Config().Value);

        Assert.True(result.Success);
        Assert.Equal("4242", result.ProviderMessageId);
        Assert.Equal("-1001234567890", result.Target);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://api.telegram.test/bot{Token}/sendMessage", request.RequestUri!.ToString());
        Assert.DoesNotContain(Token, body);   // the token is in the address only, never in the payload
    }

    [Fact]
    public async Task ThePayload_HasTheShapeTelegramExpects()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage("-1009999", "hello", ChatFormat.PlainText, "p"), Config().Value);

        var body = Body(handler);
        Assert.Equal("-1009999", body.GetProperty("chat_id").GetString());   // the recipient of the message wins over the default
        Assert.Equal("hello", body.GetProperty("text").GetString());
        Assert.True(body.GetProperty("disable_web_page_preview").GetBoolean());
    }

    [Fact]
    public async Task ADefaultChatId_IsUsed_WhenTheMessageNamesNone()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(" ", "hi", null, "p"), Config().Value);

        Assert.Equal("-1001234567890", Body(handler).GetProperty("chat_id").GetString());
    }

    [Fact]
    public async Task PlainText_IsEscapedAndSentAsMarkdownV2_SoNoCharacterCanMakeTheApiAnswer400()
    {
        var (provider, handler) = Make();
        const string text = "Disk 90% full (host-01.example.com)! #ops [urgent] a_b *c* 1+1=2";

        await provider.SendMessageAsync(new ChatMessage(null, text, ChatFormat.PlainText, "p"), Config().Value);

        var body = Body(handler);
        Assert.Equal("MarkdownV2", body.GetProperty("parse_mode").GetString());
        Assert.Equal(ChatMarkup.EscapeTelegramMarkdownV2(text), body.GetProperty("text").GetString());
        Assert.Contains("\\(host\\-01\\.example\\.com\\)\\!", body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task MarkdownV2_IsSentAsTheCallerWroteIt()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, "*bold* and _italic_", ChatFormat.MarkdownV2, "p"), Config().Value);

        var body = Body(handler);
        Assert.Equal(("MarkdownV2", "*bold* and _italic_"), (body.GetProperty("parse_mode").GetString(), body.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Markdown_IsSentWithTelegramsOlderMarkdownMode()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, "*bold*", ChatFormat.Markdown, "p"), Config().Value);

        Assert.Equal("Markdown", Body(handler).GetProperty("parse_mode").GetString());
    }

    [Fact]
    public async Task TheSafeEscapeRequest_EscapesWhateverTheFormat()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(
            new ChatMessage(null, "*not bold*", ChatFormat.MarkdownV2, "p", new Dictionary<string, string> { ["safeEscape"] = "true" }), Config().Value);

        Assert.Equal("\\*not bold\\*", Body(handler).GetProperty("text").GetString());
    }

    [Fact]
    public async Task TheConfigurationsParseMode_IsTheDefault_ForAMessageThatNamesNoFormat_ButAnExplicitPlainTextAlwaysEscapes()
    {
        var (provider, handler) = Make();
        var config = Config("""{"defaultChatId":"-1","parseMode":"MarkdownV2"}""").Value;

        await provider.SendMessageAsync(new ChatMessage(null, "*bold*", null, "p"), config);
        var byDefault = Body(handler);
        await provider.SendMessageAsync(new ChatMessage(null, "*bold*", ChatFormat.PlainText, "p"), config);
        var explicitPlain = Body(handler);

        Assert.Equal("*bold*", byDefault.GetProperty("text").GetString());          // the configuration says MarkdownV2: as written
        Assert.Equal("\\*bold\\*", explicitPlain.GetProperty("text").GetString());   // but a caller that says PlainText gets it shown as written
    }

    [Fact]
    public async Task WithoutAChatId_NothingIsSent()
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config("{}").Value);

        Assert.False(result.Success);
        Assert.Contains("chat id", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("../../x")]
    [InlineData("12 34")]
    [InlineData("-100/../bot")]
    public async Task AChatIdThatIsNotAChatId_IsRefusedBeforeAnyCall(string chat)
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(chat, "hi", null, "p"), Config().Value);

        Assert.False(result.Success);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc/def:ghi")]
    [InlineData("123456:abc?x=1")]
    public async Task ATokenThatIsMissingOrCouldChangeTheAddress_IsRefusedBeforeAnyCall(string? token)
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), RuntimeConfig.Make(ProviderType.TelegramBot, Settings, token));

        Assert.False(result.Success);
        Assert.Contains("token", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized", "rejected the bot token")]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden: bot was blocked by the user", "may not post")]
    [InlineData(HttpStatusCode.BadRequest, "Bad Request: chat not found", "chat not found")]
    public async Task Failures_BecomeShortMessages(HttpStatusCode status, string description, string expected)
    {
        var (provider, _) = Make(_ => Failure(status, description));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config().Value);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
        Assert.Equal("-1001234567890", result.Target);
    }

    [Fact]
    public async Task ARateLimit_SaysHowLongToWait()
    {
        var (provider, _) = Make(_ => Failure(HttpStatusCode.TooManyRequests, "Too Many Requests: retry after 12", ""","parameters":{"retry_after":12}"""));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config().Value);

        Assert.Contains("429", result.Error);
        Assert.Contains("12 s", result.Error);
    }

    [Fact]
    public async Task AnErrorThatEchoesTheToken_IsCleaned()
    {
        var (provider, _) = Make(_ => Failure(HttpStatusCode.BadRequest, $"Bad Request: invalid url /bot{Token}/sendMessage"));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config().Value);

        Assert.False(result.Success);
        Assert.DoesNotContain(Token, result.Error);
        Assert.DoesNotContain("fake-token-for-tests", result.Error);   // nor the secret half of it
    }

    [Fact]
    public async Task ANonJsonErrorPage_StillGivesAShortMessage()
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") });

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config().Value);

        Assert.Equal("Telegram answered 502.", result.Error);
    }

    [Fact]
    public async Task ANetworkFailure_IsAResult_WithoutTheExceptionText()
    {
        var (provider, _) = Make(_ => throw new HttpRequestException($"connection refused for https://api.telegram.test/bot{Token}/sendMessage"));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config().Value);

        Assert.Equal("Could not reach Telegram.", result.Error);
    }

    [Fact]
    public async Task TestConnection_SendsTheTestMessage_AndReportsTheOutcome()
    {
        var (provider, handler) = Make();

        var ok = await provider.TestConnectionAsync(Config().Value, "-1007", "a test message");
        Assert.True(ok.Success);
        Assert.Equal(("-1007", "a test message"), (Body(handler).GetProperty("chat_id").GetString(), Body(handler).GetProperty("text").GetString()));

        var (failing, _) = Make(_ => Failure(HttpStatusCode.Unauthorized, "Unauthorized"));
        var failed = await failing.TestConnectionAsync(Config().Value, string.Empty, "x");
        Assert.False(failed.Success);
        Assert.DoesNotContain(Token, failed.Message);
    }
}

public class DiscordWebhookChatProviderTests
{
    private static readonly string Webhook = FakeChatSecrets.DiscordWebhook;
    private static readonly string[] Hidden = ["fake-webhook-token-for-tests-0123456789", "123456789012345678"];

    private static readonly FakeHostResolver PublicDns = new(new() { ["discord.com"] = ["162.159.135.232"], ["ptb.discord.com"] = ["162.159.135.232"] });

    private static (DiscordWebhookChatProvider Provider, StubHttpHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, FakeHostResolver? dns = null, bool allowPrivate = false)
    {
        var handler = new StubHttpHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"998877"}""") }));
        var policy = new OutboundHostPolicy(Options.Create(new EmailOptions { AllowPrivateNetworkHosts = allowPrivate }), dns ?? PublicDns);
        return (new DiscordWebhookChatProvider(new HttpClient(handler), policy, NullLogger<DiscordWebhookChatProvider>.Instance), handler);
    }

    private static AppInHouse.SharedServices.Application.Configurations.ChannelRuntimeConfiguration Config(string settings = "{}", string? webhook = null) =>
        RuntimeConfig.Make(ProviderType.DiscordWebhook, settings, webhook ?? Webhook);

    private static JsonElement Body(StubHttpHandler handler) => JsonDocument.Parse(handler.Calls.Last().Body).RootElement.Clone();

    [Fact]
    public async Task ItPostsToTheWebhook_WithWaitSoTheAnswerCarriesTheMessageId()
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hello", ChatFormat.Markdown, "p"), Config());

        Assert.True(result.Success);
        Assert.Equal("998877", result.ProviderMessageId);
        var (request, _) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Webhook + "?wait=true", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ThePayload_HasTheContent_TheUsernameAndAvatar_AndSwitchesMentionsOff()
    {
        var (provider, handler) = Make();
        var settings = """{"defaultUsername":"Alerts","avatarUrl":"https://cdn.example.com/a.png"}""";

        await provider.SendMessageAsync(new ChatMessage(null, "hello @everyone", ChatFormat.Markdown, "p"), Config(settings));

        var body = Body(handler);
        Assert.Equal("hello @everyone", body.GetProperty("content").GetString());
        Assert.Equal("Alerts", body.GetProperty("username").GetString());
        Assert.Equal("https://cdn.example.com/a.png", body.GetProperty("avatar_url").GetString());
        Assert.Equal(0, body.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());   // @everyone / @here / roles ping no one
    }

    [Fact]
    public async Task WithoutUsernameOrAvatar_TheyAreLeftOut()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        var body = Body(handler);
        Assert.False(body.TryGetProperty("username", out _));
        Assert.False(body.TryGetProperty("avatar_url", out _));
    }

    [Fact]
    public async Task PlainText_HasItsMarkdownNeutralised_ButMarkdownIsLeftToRender()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, "**not bold** _x_", ChatFormat.PlainText, "p"), Config());
        var plain = Body(handler).GetProperty("content").GetString();
        await provider.SendMessageAsync(new ChatMessage(null, "**bold**", ChatFormat.Markdown, "p"), Config());
        var markdown = Body(handler).GetProperty("content").GetString();

        Assert.Equal("\\*\\*not bold\\*\\* \\_x\\_", plain);
        Assert.Equal("**bold**", markdown);
    }

    [Fact]
    public async Task TheContent_IsCutAtTheLimitDiscordAccepts()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, new string('a', 3000), ChatFormat.Markdown, "p"), Config());

        var content = Body(handler).GetProperty("content").GetString()!;
        Assert.Equal(DiscordWebhookChatProvider.MaxContentLength, content.Length);
        Assert.EndsWith("…", content);
    }

    [Fact]
    public async Task ATextExactlyAtTheLimit_IsNotCut()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, new string('a', 2000), ChatFormat.Markdown, "p"), Config());

        Assert.Equal(2000, Body(handler).GetProperty("content").GetString()!.Length);
    }

    [Fact]
    public async Task An204_WithNoBody_IsAccepted()
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.True(result.Success);
        Assert.Null(result.ProviderMessageId);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "does not accept this webhook")]
    [InlineData(HttpStatusCode.Unauthorized, "does not accept this webhook")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting the rate")]
    [InlineData(HttpStatusCode.BadRequest, "answered 400")]
    [InlineData(HttpStatusCode.Redirect, "redirect")]
    public async Task Failures_BecomeShortMessages_WithoutTheAddressOrItsToken(HttpStatusCode status, string expected)
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(status) { Content = new StringContent($"{{\"message\":\"Unknown Webhook {Webhook}\"}}") });

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
        Assert.DoesNotContain(Webhook, result.Error);
        foreach (var part in Hidden) Assert.DoesNotContain(part, result.Error);
    }

    [Fact]
    public async Task ANetworkFailure_IsAResult_WithoutTheAddress()
    {
        var (provider, _) = Make(_ => throw new HttpRequestException($"failed {Webhook}"));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.Equal("Could not reach Discord.", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://169.254.169.254/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    public async Task AnAddressThatIsNotADiscordWebhook_IsRefused_BeforeAnyCall(string? webhook)
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), RuntimeConfig.Make(ProviderType.DiscordWebhook, "{}", webhook));

        Assert.False(result.Success);
        Assert.Contains("not a Discord webhook address", result.Error);
        Assert.Empty(handler.Calls);
    }

    // ------------------------------------------------------------------------------------------------ SSRF: where the name points

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("172.16.4.1")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.1.1")]
    [InlineData("::1")]
    [InlineData("fd00::7")]
    public async Task ANameThatResolvesToAPrivateOrLoopbackAddress_IsRefused_BeforeAnyCall(string address)
    {
        var (provider, handler) = Make(dns: new(new() { ["discord.com"] = [address] }));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.False(result.Success);
        Assert.Contains("private or loopback", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("169.254.169.254")]    // the cloud metadata address
    [InlineData("169.254.10.10")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.9")]
    public async Task ANameThatResolvesToAMetadataLinkLocalOrMulticastAddress_IsAlwaysRefused(string address)
    {
        var (provider, handler) = Make(dns: new(new() { ["discord.com"] = [address] }), allowPrivate: true);   // even where private networks are allowed

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.False(result.Success);
        Assert.Contains("not allowed", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ANameWithOneBadAddressAmongGoodOnes_IsRefused()
    {
        // A name that answers with a public address and a private one (a rebinding trick) is not trusted.
        var (provider, handler) = Make(dns: new(new() { ["discord.com"] = ["162.159.135.232", "10.0.0.5"] }));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.False(result.Success);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task APublicAddress_IsFine()
    {
        var (provider, handler) = Make(dns: new(new() { ["discord.com"] = ["162.159.135.232"] }));

        Assert.True((await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config())).Success);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ANameThatDoesNotResolve_IsAResult()
    {
        var (provider, handler) = Make(dns: new(new()));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.False(result.Success);
        Assert.Contains("cannot be resolved", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task TestConnection_SendsTheTestMessage()
    {
        var (provider, handler) = Make();

        var ok = await provider.TestConnectionAsync(Config(), string.Empty, "a test message");

        Assert.True(ok.Success);
        Assert.Equal("a test message", Body(handler).GetProperty("content").GetString());
    }
}

public class SlackWebhookChatProviderTests
{
    private static readonly string Webhook = FakeChatSecrets.SlackWebhook;

    private static readonly FakeHostResolver PublicDns = new(new() { ["hooks.slack.com"] = ["54.230.1.2"] });

    private static (SlackWebhookChatProvider Provider, StubHttpHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, FakeHostResolver? dns = null)
    {
        var handler = new StubHttpHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }));
        var policy = new OutboundHostPolicy(Options.Create(new EmailOptions()), dns ?? PublicDns);
        return (new SlackWebhookChatProvider(new HttpClient(handler), policy, NullLogger<SlackWebhookChatProvider>.Instance), handler);
    }

    private static AppInHouse.SharedServices.Application.Configurations.ChannelRuntimeConfiguration Config(string settings = "{}", string? webhook = null) =>
        RuntimeConfig.Make(ProviderType.SlackWebhook, settings, webhook ?? Webhook);

    private static JsonElement Body(StubHttpHandler handler) => JsonDocument.Parse(handler.Calls.Last().Body).RootElement.Clone();

    [Fact]
    public async Task ItPostsTheTextToTheWebhookAddress()
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hello", ChatFormat.Markdown, "p"), Config());

        Assert.True(result.Success);
        var (request, _) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Webhook, request.RequestUri!.ToString());
        Assert.Equal("hello", Body(handler).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ThePayload_CarriesTheChannelOverrideAndTheEmoji()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config("""{"defaultChannel":"#alerts","iconEmoji":":warning:"}"""));

        var body = Body(handler);
        Assert.Equal("#alerts", body.GetProperty("channel").GetString());
        Assert.Equal(":warning:", body.GetProperty("icon_emoji").GetString());
    }

    [Fact]
    public async Task ARecipientOnTheMessage_OverridesTheDefaultChannel()
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage("#other", "hi", null, "p"), Config("""{"defaultChannel":"#alerts"}"""));

        Assert.Equal("#other", Body(handler).GetProperty("channel").GetString());
        Assert.Equal("#other", result.Target);
    }

    [Fact]
    public async Task WithoutAChannel_ItIsLeftOut_AndTheWebhooksOwnChannelIsUsed()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.False(Body(handler).TryGetProperty("channel", out _));
        Assert.False(Body(handler).TryGetProperty("icon_emoji", out _));
    }

    [Fact]
    public async Task Ampersand_AndAngleBrackets_AreAlwaysEscaped_SoTheTextCannotCarryLinksOrMentions()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, "<!channel> & <https://x.test|click>", ChatFormat.Markdown, "p"), Config());

        Assert.Equal("&lt;!channel&gt; &amp; &lt;https://x.test|click&gt;", Body(handler).GetProperty("text").GetString());
    }

    [Fact]
    public async Task MarkdownIsRendered_OnlyWhenAsked()
    {
        var (provider, handler) = Make();

        await provider.SendMessageAsync(new ChatMessage(null, "*b*", ChatFormat.PlainText, "p"), Config());
        var plain = Body(handler).GetProperty("mrkdwn").GetBoolean();
        await provider.SendMessageAsync(new ChatMessage(null, "*b*", ChatFormat.Markdown, "p"), Config());
        var markdown = Body(handler).GetProperty("mrkdwn").GetBoolean();
        await provider.SendMessageAsync(new ChatMessage(null, "*b*", null, "p"), Config());
        var byDefault = Body(handler).GetProperty("mrkdwn").GetBoolean();

        Assert.False(plain);
        Assert.True(markdown);
        Assert.False(byDefault);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("#a/b")]
    public async Task AChannelThatIsNotAChannel_IsRefused_BeforeAnyCall(string channel)
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(channel, "hi", null, "p"), Config());

        Assert.False(result.Success);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_payload", "invalid_payload")]
    [InlineData(HttpStatusCode.NotFound, "no_service", "no_service")]
    [InlineData(HttpStatusCode.Forbidden, "action_prohibited", "action_prohibited")]
    [InlineData(HttpStatusCode.Gone, "channel_is_archived", "channel_is_archived")]
    public async Task Failures_ShowSlacksShortCode(HttpStatusCode status, string body, string expected)
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
        Assert.DoesNotContain(Webhook, result.Error);
    }

    [Fact]
    public async Task ABodyThatIsNotAShortCode_IsNotShown()
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent($"<html>{Webhook}</html>") });

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.Equal("Slack answered 502.", result.Error);
    }

    [Fact]
    public async Task ARateLimit_IsReported()
    {
        var (provider, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        Assert.Contains("limiting the rate", (await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config())).Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("http://hooks.slack.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("https://169.254.169.254/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    public async Task AnAddressThatIsNotASlackWebhook_IsRefused_BeforeAnyCall(string? webhook)
    {
        var (provider, handler) = Make();

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), RuntimeConfig.Make(ProviderType.SlackWebhook, "{}", webhook));

        Assert.False(result.Success);
        Assert.Contains("not a Slack webhook address", result.Error);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("127.0.0.1")]
    public async Task ANameThatResolvesToAnInternalAddress_IsRefused_BeforeAnyCall(string address)
    {
        var (provider, handler) = Make(dns: new(new() { ["hooks.slack.com"] = [address] }));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.False(result.Success);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ANetworkFailure_IsAResult_WithoutTheAddress()
    {
        var (provider, _) = Make(_ => throw new HttpRequestException($"failed {Webhook}"));

        var result = await provider.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), Config());

        Assert.Equal("Could not reach Slack.", result.Error);
    }

    [Fact]
    public async Task TestConnection_SendsTheTestMessage()
    {
        var (provider, handler) = Make();

        var ok = await provider.TestConnectionAsync(Config(), "#qa", "a test message");

        Assert.True(ok.Success);
        Assert.Equal(("#qa", "a test message"), (Body(handler).GetProperty("channel").GetString(), Body(handler).GetProperty("text").GetString()));
    }
}

/// <summary>The bot token and the webhook address travel in the request address; they must not reach a log (the framework logs request addresses unless told not to).</summary>
public class ChatProviderLoggingTests
{
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Lines);

        public void Dispose() { }

        private sealed class Capture(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines) lines.Add($"{category} [{logLevel}] {formatter(state, exception)} {exception}");
            }
        }
    }

    private static ServiceProvider Build(CapturingLoggerProvider capture, StubHttpHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:AllowPrivateNetworkHosts"] = "false",
            ["Chat:TelegramBaseUrl"] = "https://api.telegram.test",
        }).Build());
        services.AddSharedServicesInfrastructure(new ConfigurationBuilder().Build(), Path.GetTempPath());
        services.Configure<ChatOptions>(o => o.TelegramBaseUrl = "https://api.telegram.test");
        services.AddSingleton<IHostAddressResolver>(new FakeHostResolver(new() { ["discord.com"] = ["162.159.135.232"], ["hooks.slack.com"] = ["54.230.1.2"] }));

        // The real wiring, except that the network is replaced by the stub (the logging handlers stay where the wiring put them).
        services.AddHttpClient<TelegramBotChatProvider>().ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient<DiscordWebhookChatProvider>().ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient<SlackWebhookChatProvider>().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SendingThroughTheRealWiring_LogsNeitherTheTokenNorTheWebhookAddress()
    {
        var capture = new CapturingLoggerProvider();
        var handler = new StubHttpHandler(request => request.RequestUri!.Host.Contains("telegram")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"result":{"message_id":1}}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var provider = Build(capture, handler);

        var telegram = provider.GetServices<IChatProvider>().Single(p => p.Type == ProviderType.TelegramBot);
        var discord = provider.GetServices<IChatProvider>().Single(p => p.Type == ProviderType.DiscordWebhook);
        var slack = provider.GetServices<IChatProvider>().Single(p => p.Type == ProviderType.SlackWebhook);
        await telegram.SendMessageAsync(new ChatMessage("-1001", "hi", null, "p"), RuntimeConfig.Make(ProviderType.TelegramBot, "{}", FakeChatSecrets.TelegramToken));
        await discord.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), RuntimeConfig.Make(ProviderType.DiscordWebhook, "{}", FakeChatSecrets.DiscordWebhook));
        await slack.SendMessageAsync(new ChatMessage(null, "hi", null, "p"), RuntimeConfig.Make(ProviderType.SlackWebhook, "{}", FakeChatSecrets.SlackWebhook));

        Assert.Equal(3, handler.Calls.Count);   // all three really sent
        var everything = string.Join('\n', capture.Lines);
        Assert.DoesNotContain("fake-token-for-tests", everything);
        Assert.DoesNotContain("fake-webhook-token-for-tests", everything);
        Assert.DoesNotContain("fakewebhooktokenfortests", everything);
        Assert.DoesNotContain("123456789012345678", everything);
        Assert.DoesNotContain("/sendMessage", everything);   // not even the shape of the address
    }

    [Fact]
    public async Task FailuresAreLoggedToo_WithoutTheSecrets()
    {
        var capture = new CapturingLoggerProvider();
        var handler = new StubHttpHandler(request => throw new HttpRequestException($"connection reset for {request.RequestUri}"));
        using var provider = Build(capture, handler);

        foreach (var chat in provider.GetServices<IChatProvider>().Where(p => p.Type != ProviderType.Mock))
        {
            var secret = FakeChatSecrets.For(chat.Type);
            var result = await chat.SendMessageAsync(new ChatMessage("-1001", "hi", null, "p"), RuntimeConfig.Make(chat.Type, "{}", secret));
            Assert.False(result.Success);
            Assert.DoesNotContain("fake-", result.Error);
        }

        var everything = string.Join('\n', capture.Lines);
        Assert.DoesNotContain("fake-token-for-tests", everything);
        Assert.DoesNotContain("fake-webhook-token-for-tests", everything);
        Assert.DoesNotContain("fakewebhooktokenfortests", everything);
    }

    [Fact]
    public void TheRealProviders_FollowNoRedirect_AndAllFourAreRegistered()
    {
        var capture = new CapturingLoggerProvider();
        using var provider = Build(capture, new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var types = provider.GetServices<IChatProvider>().Select(p => p.Type).Order().ToList();

        Assert.Equal(new[] { ProviderType.TelegramBot, ProviderType.Mock, ProviderType.DiscordWebhook, ProviderType.SlackWebhook }.Order(), types);
    }
}
