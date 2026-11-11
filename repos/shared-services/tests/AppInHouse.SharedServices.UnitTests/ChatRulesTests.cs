using System.Text.Json;
using AppInHouse.SharedServices.Application.Chat;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;

namespace AppInHouse.SharedServices.UnitTests;

public class ChatMarkupTests
{
    // The characters Telegram's MarkdownV2 gives a meaning to: all of them must be escaped or the API answers 400 "can't parse entities".
    public static TheoryData<char> TelegramSpecials => new()
    {
        '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!', '\\',
    };

    [Theory]
    [MemberData(nameof(TelegramSpecials))]
    public void EveryMarkdownV2Special_IsEscapedWithABackslash(char special) =>
        Assert.Equal($"a\\{special}b", ChatMarkup.EscapeTelegramMarkdownV2($"a{special}b"));

    [Fact]
    public void TheTaskListOfSpecials_IsAllEscaped()
    {
        const string list = "_ * [ ] ( ) ~ > # + - = | { } . !";

        var escaped = ChatMarkup.EscapeTelegramMarkdownV2(list);

        foreach (var c in list.Where(c => c != ' '))
            Assert.Contains($"\\{c}", escaped);
        Assert.Equal(list.Length + list.Count(c => c != ' '), escaped.Length);   // exactly one backslash per special, nothing else added
    }

    [Theory]
    [InlineData("Hello world")]
    [InlineData("Xin chào, đây là tin nhắn tiếng Việt: ổn định 100%")]
    [InlineData("emoji 🚀 and tab\tand\nnewline")]
    [InlineData("quotes \"double\" 'single' @user $price ^caret & amp ? query , semi ; colon :")]
    public void OrdinaryText_IsLeftAlone(string text) => Assert.Equal(text, ChatMarkup.EscapeTelegramMarkdownV2(text));

    [Fact]
    public void ARealAlert_WithEverySpecial_ComesOutParseable()
    {
        const string alert = "Disk 90% full on host-01.example.com (/dev/sda1)! Free: 12.5GB [warning] #ops _urgent_ *now* ~maybe~ a|b {x} 1+1=2 > 0 `code` back\\slash";

        var escaped = ChatMarkup.EscapeTelegramMarkdownV2(alert);

        // Every special is preceded by a backslash: walk the text and check no special stands alone.
        for (var i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == '\\') { i++; continue; }                                          // an escaped character: skip it
            Assert.DoesNotContain(escaped[i], "_*[]()~`>#+-=|{}.!");                           // anything else is an ordinary character
        }
    }

    [Fact]
    public void Escaping_CanBeUndone_ByRemovingEachBackslashBeforeAnyCharacter()
    {
        const string text = "a_b*c[d]e(f)g~h`i>j#k+l-m=n|o{p}q.r!s\\t";
        var escaped = ChatMarkup.EscapeTelegramMarkdownV2(text);

        var restored = System.Text.RegularExpressions.Regex.Replace(escaped, @"\\(.)", "$1");

        Assert.Equal(text, restored);
    }

    [Fact]
    public void Slack_EscapesTheThreeCharactersThatStartLinksAndMentions()
    {
        Assert.Equal("&lt;!channel&gt; &amp; &lt;https://x.test|click&gt;", ChatMarkup.EscapeSlack("<!channel> & <https://x.test|click>"));
        Assert.Equal("&amp;amp;", ChatMarkup.EscapeSlack("&amp;"));   // an existing entity is escaped too: the text is shown as written
        Assert.Equal("plain text *bold stays*", ChatMarkup.EscapeSlack("plain text *bold stays*"));
    }

    [Fact]
    public void Discord_EscapesItsMarkdown()
    {
        Assert.Equal("\\*\\*bold\\*\\* \\_i\\_ \\~s\\~ \\`c\\` \\|\\|spoiler\\|\\| \\> quote \\# head", ChatMarkup.EscapeDiscord("**bold** _i_ ~s~ `c` ||spoiler|| > quote # head"));
        Assert.Equal("plain text", ChatMarkup.EscapeDiscord("plain text"));
    }
}

public class ChatSettingsReaderTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Settings_AreRead()
    {
        var settings = ChatSettingsReader.Read(Json("""{"defaultChatId":"-1001234567890","parseMode":"MarkdownV2","defaultUsername":"Alerts","avatarUrl":"https://cdn.example.com/a.png","defaultChannel":"#ops","iconEmoji":":warning:"}"""));

        Assert.Equal(("-1001234567890", TelegramParseMode.MarkdownV2, "Alerts", "https://cdn.example.com/a.png", "#ops", ":warning:"),
            (settings.DefaultChatId, settings.ParseMode, settings.DefaultUsername, settings.AvatarUrl, settings.DefaultChannel, settings.IconEmoji));
    }

    [Fact]
    public void EmptySettings_GetPlainTextAndNothingElse()
    {
        var settings = ChatSettingsReader.Read(Json("{}"));

        Assert.Equal((null, TelegramParseMode.PlainText, null, null, null, null),
            (settings.DefaultChatId, settings.ParseMode, settings.DefaultUsername, settings.AvatarUrl, settings.DefaultChannel, settings.IconEmoji));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"defaultChatId":"123456789"}""")]
    [InlineData("""{"defaultChatId":"-1001234567890"}""")]
    [InlineData("""{"defaultChatId":"@my_channel"}""")]
    [InlineData("""{"parseMode":"plaintext"}""")]
    [InlineData("""{"avatarUrl":"https://cdn.example.com/a.png"}""")]
    [InlineData("""{"defaultChannel":"#alerts"}""")]
    [InlineData("""{"defaultChannel":"C0123456789"}""")]
    [InlineData("""{"iconEmoji":":rotating_light:"}""")]
    public void GoodSettings_HaveNoProblems(string settings) => Assert.Empty(ChatSettingsReader.Validate(Json(settings)));

    [Theory]
    [InlineData("""{"defaultChatId":"not a chat"}""")]
    [InlineData("""{"defaultChatId":"@ab"}""")]
    [InlineData("""{"defaultChatId":12345}""")]
    [InlineData("""{"parseMode":"HTML"}""")]
    [InlineData("""{"parseMode":"Markdown"}""")]
    [InlineData("""{"defaultUsername":"line\nbreak"}""")]
    [InlineData("""{"avatarUrl":"http://cdn.example.com/a.png"}""")]
    [InlineData("""{"avatarUrl":"https://user:pass@cdn.example.com/a.png"}""")]
    [InlineData("""{"avatarUrl":"javascript:alert(1)"}""")]
    [InlineData("""{"defaultChannel":"two words"}""")]
    [InlineData("""{"iconEmoji":"warning"}""")]
    [InlineData("""{"iconEmoji":":a b:"}""")]
    public void BadSettings_AreReported(string settings) => Assert.NotEmpty(ChatSettingsReader.Validate(Json(settings)));

    [Fact]
    public void SettingsThatAreNotAnObject_AreRefused() =>
        Assert.Single(ChatSettingsReader.Validate(Json("[1]")));
}

public class ChatSecretRulesTests
{
    [Fact]
    public void AGoodBotToken_IsAccepted() =>
        Assert.Null(ChatSecretRules.Check(ProviderType.TelegramBot, FakeChatSecrets.TelegramToken));

    [Theory]
    [InlineData("123:token")]
    [InlineData("123456:abcdefghij")]
    public void ShortButWellFormedTokens_AreAccepted(string token) =>
        Assert.Null(ChatSecretRules.Check(ProviderType.TelegramBot, token));

    [Theory]
    [InlineData("")]
    [InlineData("no-colon-here")]
    [InlineData("123456:abc/def")]                  // a "/" would change the path of the request
    [InlineData("123456:abc?x=1")]                  // so would a "?"
    [InlineData("123456:abc#frag")]
    [InlineData("123456:../../etc")]
    [InlineData("123456:abc def")]
    [InlineData("123456:abc\ndef")]
    [InlineData("https://api.telegram.org/bot123456:abcdef/sendMessage")]
    [InlineData("123456:")]
    [InlineData(":abcdef")]
    public void ATokenThatCouldChangeTheRequestAddress_IsRefused(string token) =>
        Assert.NotNull(ChatSecretRules.Check(ProviderType.TelegramBot, token));

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://discordapp.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://ptb.discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://canary.discord.com/api/v10/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://DISCORD.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789/")]
    public void ADiscordWebhookAddress_IsAccepted(string url)
    {
        Assert.Null(ChatSecretRules.Check(ProviderType.DiscordWebhook, url));
        Assert.True(ChatSecretRules.TryParseWebhook(ProviderType.DiscordWebhook, url, out var uri));
        Assert.Equal("https", uri.Scheme);
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]                  // not https
    [InlineData("https://evil.example.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]            // not Discord
    [InlineData("https://discord.com.evil.example.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")] // a lookalike host
    [InlineData("https://notdiscord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://user:pass@discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]      // a login in the address
    [InlineData("https://discord.com:8443/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]            // another port
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789?x=1")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789#f")]
    [InlineData("https://discord.com/other/path/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://discord.com/api/webhooks/notanumber/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/short")]
    [InlineData("https://169.254.169.254/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]            // the metadata address
    [InlineData("https://127.0.0.1/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://localhost/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://10.0.0.5/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ftp://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789 extra")]
    [InlineData("")]
    public void ADiscordAddressThatCouldReachAnotherServer_IsRefused(string url)
    {
        Assert.NotNull(ChatSecretRules.Check(ProviderType.DiscordWebhook, url));
        Assert.False(ChatSecretRules.TryParseWebhook(ProviderType.DiscordWebhook, url, out _));
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("https://hooks.slack.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123/")]
    public void ASlackWebhookAddress_IsAccepted(string url) =>
        Assert.True(ChatSecretRules.TryParseWebhook(ProviderType.SlackWebhook, url, out _));

    [Theory]
    [InlineData("http://hooks.slack.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("https://hooks.slack.com.evil.example.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("https://evil.example.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("https://hooks.slack.com/services/T0FAKE000/B0FAKE000")]
    [InlineData("https://hooks.slack.com/other/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("https://hooks.slack.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123?x=1")]
    [InlineData("https://hooks.slack.com:444/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("https://169.254.169.254/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789")]               // a Discord address is not a Slack one
    public void ASlackAddressThatCouldReachAnotherServer_IsRefused(string url) =>
        Assert.False(ChatSecretRules.TryParseWebhook(ProviderType.SlackWebhook, url, out _));

    [Fact]
    public void TheMessage_NeverRepeatsTheValueItRefused()
    {
        const string secretPart = "very-secret-part-0123456789abcdef";
        var url = "https://evil.example.com/api/webhooks/123456789012345678/" + secretPart;

        var message = ChatSecretRules.Check(ProviderType.DiscordWebhook, url);

        Assert.NotNull(message);
        Assert.DoesNotContain(secretPart, message);
        Assert.DoesNotContain("evil.example.com", message);
    }

    [Fact]
    public void OtherProviders_HaveNoShapeToCheck() =>
        Assert.Null(ChatSecretRules.Check(ProviderType.SendGrid, "whatever"));
}

public class ChatConfigurationValidationTests
{
    private static SaveChannelConfigurationCommand Command(ProviderType provider, string settings = "{}", string? secret = null, ChannelType channel = ChannelType.Chat) =>
        new("p", channel, provider, "Chat", settings, secret);

    [Fact]
    public void AnInvalidSecretShape_IsRefusedOnSave_ForEachChatProvider()
    {
        foreach (var (provider, bad) in new[]
        {
            (ProviderType.TelegramBot, "not a token"),
            (ProviderType.DiscordWebhook, "https://evil.example.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz0123456789"),
            (ProviderType.SlackWebhook, "http://hooks.slack.com/services/T0FAKE000/B0FAKE000/fakewebhooktokenfortests0123"),
        })
        {
            var errors = ChannelConfigurationValidator.Validate(Command(provider, secret: bad));

            Assert.Contains(nameof(SaveChannelConfigurationCommand.Secret), errors.Keys);
            Assert.DoesNotContain(bad, string.Join(' ', errors.SelectMany(e => e.Value)));
        }
    }

    [Fact]
    public void AGoodSecret_AndTheMask_AreFine_ForEachChatProvider()
    {
        foreach (var provider in new[] { ProviderType.TelegramBot, ProviderType.DiscordWebhook, ProviderType.SlackWebhook })
        {
            Assert.Empty(ChannelConfigurationValidator.Validate(Command(provider, secret: FakeChatSecrets.For(provider))));
            Assert.Empty(ChannelConfigurationValidator.Validate(Command(provider, secret: "••••••••")));   // "keep the stored one"
            Assert.Empty(ChannelConfigurationValidator.Validate(Command(provider, secret: null)));         // a draft may be saved without one
        }
    }

    [Theory]
    [InlineData("""{"defaultChatId":"nope"}""")]
    [InlineData("""{"parseMode":"HTML"}""")]
    [InlineData("""{"avatarUrl":"http://x.test/a.png"}""")]
    [InlineData("""{"iconEmoji":"warning"}""")]
    public void BadChatSettings_AreRefusedOnSave_ForBothChatChannels(string settings)
    {
        Assert.Contains("SettingsJson", ChannelConfigurationValidator.Validate(Command(ProviderType.TelegramBot, settings)).Keys);
        Assert.Contains("SettingsJson", ChannelConfigurationValidator.Validate(Command(ProviderType.TelegramBot, settings, channel: ChannelType.Telegram)).Keys);
    }

    [Fact]
    public void TheChatRules_DoNotApplyToEmail()
    {
        // "defaultChatId" means nothing to an email configuration: it is not judged as a chat setting.
        var email = new SaveChannelConfigurationCommand("p", ChannelType.Email, ProviderType.Mock, "Mail", """{"defaultChatId":"nope"}""", null);

        Assert.Empty(ChannelConfigurationValidator.Validate(email));
    }

    [Fact]
    public void ASecretInsideTheChatSettings_IsStillRefused()
    {
        var errors = ChannelConfigurationValidator.Validate(Command(ProviderType.DiscordWebhook, """{"webhookToken":"x"}"""));

        Assert.Contains("SettingsJson", errors.Keys);
    }

    [Fact]
    public void ChatAndTheOlderTelegramChannel_AcceptTheSameProviders()
    {
        Assert.True(ProviderCatalog.Supports(ChannelType.Chat, ProviderType.TelegramBot));
        Assert.True(ProviderCatalog.Supports(ChannelType.Chat, ProviderType.DiscordWebhook));
        Assert.True(ProviderCatalog.Supports(ChannelType.Chat, ProviderType.SlackWebhook));
        Assert.True(ProviderCatalog.Supports(ChannelType.Chat, ProviderType.Mock));
        Assert.True(ProviderCatalog.Supports(ChannelType.Telegram, ProviderType.TelegramBot));   // configurations saved before Chat existed keep working
        Assert.False(ProviderCatalog.Supports(ChannelType.Chat, ProviderType.Smtp));
        Assert.False(ProviderCatalog.Supports(ChannelType.Email, ProviderType.DiscordWebhook));
        Assert.Equal([ChannelType.Chat, ChannelType.Telegram], ProviderCatalog.ChatChannels);
    }

    [Fact]
    public void TheWebhookProviders_NeedASecret_BecauseTheAddressIsTheSecret()
    {
        Assert.True(ProviderCatalog.RequiresSecret(ProviderType.TelegramBot));
        Assert.True(ProviderCatalog.RequiresSecret(ProviderType.DiscordWebhook));
        Assert.True(ProviderCatalog.RequiresSecret(ProviderType.SlackWebhook));
        Assert.False(ProviderCatalog.RequiresSecret(ProviderType.Mock));
    }
}
