using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests;

public class ChannelConfigurationValidatorTests
{
    private static SaveChannelConfigurationCommand Valid(string settings = """{"host":"smtp.example.com","port":587}""") =>
        new("linguabridge", ChannelType.Email, ProviderType.Smtp, "Main", settings, "secret");

    private static IReadOnlyDictionary<string, string[]> Check(SaveChannelConfigurationCommand command) => ChannelConfigurationValidator.Validate(command);

    [Fact]
    public void AValidCommand_HasNoErrors() => Assert.Empty(Check(Valid()));

    [Theory]
    [InlineData("linguabridge")]
    [InlineData("a")]
    [InlineData("shop-2")]
    [InlineData("my_project")]
    [InlineData("  Default  ")]   // trimmed and lower-cased before the check
    public void GoodProjectCodes_Pass(string code) => Assert.Empty(Check(Valid() with { ProjectCode = code }));

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("slash/inside")]
    [InlineData("dot.inside")]
    [InlineData("ünicode")]
    public void BadProjectCodes_AreRefused(string code) =>
        Assert.Contains(nameof(SaveChannelConfigurationCommand.ProjectCode), Check(Valid() with { ProjectCode = code }).Keys);

    [Fact]
    public void ProjectCodeLongerThanFifty_IsRefused() =>
        Assert.Contains(nameof(SaveChannelConfigurationCommand.ProjectCode), Check(Valid() with { ProjectCode = new string('a', 51) }).Keys);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankName_IsRefused(string name) =>
        Assert.Contains(nameof(SaveChannelConfigurationCommand.Name), Check(Valid() with { Name = name }).Keys);

    [Fact]
    public void ANameLongerThanOneHundred_IsRefused() =>
        Assert.Contains(nameof(SaveChannelConfigurationCommand.Name), Check(Valid() with { Name = new string('n', 101) }).Keys);

    [Theory]
    [InlineData(ChannelType.Email, ProviderType.S3)]
    [InlineData(ChannelType.Email, ProviderType.TelegramBot)]
    [InlineData(ChannelType.Email, ProviderType.DiscordWebhook)]
    [InlineData(ChannelType.Telegram, ProviderType.Smtp)]
    [InlineData(ChannelType.Telegram, ProviderType.SlackWebhook)]
    [InlineData(ChannelType.Chat, ProviderType.Smtp)]
    [InlineData(ChannelType.Chat, ProviderType.S3)]
    [InlineData(ChannelType.Storage, ProviderType.SendGrid)]
    [InlineData(ChannelType.Storage, ProviderType.DiscordWebhook)]
    public void AProviderThatCannotServeTheChannel_IsRefused(ChannelType channel, ProviderType provider) =>
        Assert.Contains(nameof(SaveChannelConfigurationCommand.ProviderType), Check(Valid() with { ChannelType = channel, ProviderType = provider }).Keys);

    [Theory]
    [InlineData(ChannelType.Email, ProviderType.Smtp)]
    [InlineData(ChannelType.Email, ProviderType.SendGrid)]
    [InlineData(ChannelType.Email, ProviderType.Brevo)]
    [InlineData(ChannelType.Telegram, ProviderType.TelegramBot)]
    [InlineData(ChannelType.Chat, ProviderType.TelegramBot)]
    [InlineData(ChannelType.Chat, ProviderType.DiscordWebhook)]
    [InlineData(ChannelType.Chat, ProviderType.SlackWebhook)]
    [InlineData(ChannelType.Chat, ProviderType.Mock)]
    [InlineData(ChannelType.Storage, ProviderType.LocalDisk)]
    [InlineData(ChannelType.Storage, ProviderType.S3)]
    [InlineData(ChannelType.Storage, ProviderType.Mock)]
    public void EveryDocumentedPairing_Passes(ChannelType channel, ProviderType provider) =>
        Assert.Empty(Check(Valid() with { ChannelType = channel, ProviderType = provider, Secret = SecretFor(provider) }));

    // A secret of the shape each provider expects (a bot token, a webhook address), built from pieces so no real-looking key sits in this file.
    internal static string SecretFor(ProviderType provider) => provider switch
    {
        ProviderType.TelegramBot => "123456" + ":" + "fake-token-for-tests-0123456789",
        ProviderType.S3 => "fake-secret-access-key-for-tests-0123456789",
        ProviderType.DiscordWebhook => "https://discord.com/api/webhooks/" + "123456789012345678" + "/" + "fake-webhook-token-for-tests-0123456789",
        ProviderType.SlackWebhook => "https://hooks.slack.com/services/" + "T0FAKE000" + "/" + "B0FAKE000" + "/" + "fakewebhooktokenfortests0123",
        _ => "secret",
    };

    [Theory]
    [InlineData("""{"host":"h"}""")]
    [InlineData("{}")]
    [InlineData("""{"smtp":{"host":"h","ports":[25,587]},"accessKeyId":"AKIAEXAMPLE"}""")]   // an access key ID is not a secret
    [InlineData("")]
    public void AcceptableSettings_Pass(string settings) => Assert.Empty(Check(Valid(settings)));

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("""{"a":""")]
    public void SettingsThatAreNotAJsonObject_AreRefused(string settings) =>
        Assert.Contains(nameof(SaveChannelConfigurationCommand.SettingsJson), Check(Valid(settings)).Keys);

    [Theory]
    [InlineData("""{"password":"x"}""")]
    [InlineData("""{"Password":"x"}""")]
    [InlineData("""{"smtpPassword":"x"}""")]
    [InlineData("""{"botToken":"x"}""")]
    [InlineData("""{"apiKey":"x"}""")]
    [InlineData("""{"api_key":"x"}""")]
    [InlineData("""{"secretAccessKey":"x"}""")]
    [InlineData("""{"privateKey":"x"}""")]
    [InlineData("""{"Authorization":"Bearer x"}""")]
    [InlineData("""{"auth":{"credentials":{"user":"u"}}}""")]
    [InlineData("""{"items":[{"clientSecret":"x"}]}""")]
    public void SettingsThatNameASecret_AreRefused(string settings)
    {
        var errors = Check(Valid(settings));

        Assert.Contains(nameof(SaveChannelConfigurationCommand.SettingsJson), errors.Keys);
        Assert.Contains("secret", string.Join(' ', errors[nameof(SaveChannelConfigurationCommand.SettingsJson)]), StringComparison.OrdinalIgnoreCase);
    }

    // Built from pieces so this file does not contain a string that a secret scanner would take for a real key.
    private const string FakeBotToken = "123456789" + ":" + "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDs" + "aw0";
    private const string FakeSendGridKey = "SG" + "." + "abcdefghijklmnopqrstuv" + "." + "abcdefghijklmnopqrstuv";
    private const string FakePemHeader = "-----BEGIN RSA " + "PRIVATE KEY-----";

    [Theory]
    [InlineData($$"""{"note":"{{FakeBotToken}}"}""")]                 // a Telegram bot token
    [InlineData($$"""{"note":"{{FakeSendGridKey}}"}""")]              // a SendGrid key
    [InlineData($$"""{"note":"{{FakePemHeader}}\nMIIE..."}""")]       // a PEM private key (\n is a JSON escape)
    public void ValuesThatLookLikeASecret_AreRefusedWhateverThePropertyIsCalled(string settings) =>
        Assert.Contains(nameof(SaveChannelConfigurationCommand.SettingsJson), Check(Valid(settings)).Keys);

    [Fact]
    public void ErrorMessages_NameThePathButNeverRepeatTheValue()
    {
        var errors = Check(Valid("""{"smtp":{"password":"hunter2-do-not-leak"}}"""));

        var message = string.Join(' ', errors[nameof(SaveChannelConfigurationCommand.SettingsJson)]);
        Assert.Contains("smtp.password", message);
        Assert.DoesNotContain("hunter2", message);
    }

    [Fact]
    public void SettingsLargerThanSixteenKilobytes_AreRefused()
    {
        var big = $$"""{"blob":"{{new string('x', ChannelConfigurationValidator.MaxSettingsBytes)}}"}""";

        Assert.Contains(nameof(SaveChannelConfigurationCommand.SettingsJson), Check(Valid(big)).Keys);
    }

    [Fact]
    public void SettingsNestedTooDeep_AreRefused()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 12)) + "1" + new string('}', 12);

        Assert.Contains(nameof(SaveChannelConfigurationCommand.SettingsJson), Check(Valid(deep)).Keys);
    }

    [Fact]
    public void AnAbsurdlyLongSecret_IsRefused() =>
        Assert.Contains(nameof(SaveChannelConfigurationCommand.Secret), Check(Valid() with { Secret = new string('s', 5000) }).Keys);

    [Fact]
    public void UnknownEnumValues_AreRefused()
    {
        var errors = Check(Valid() with { ChannelType = (ChannelType)99 });

        Assert.Contains(nameof(SaveChannelConfigurationCommand.ChannelType), errors.Keys);
    }
}
