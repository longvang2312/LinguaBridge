using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Notifications.Bots;

/// <summary>How Telegram should read the text of a message that has no format of its own.</summary>
public enum TelegramParseMode
{
    PlainText,
    MarkdownV2,
}

/// <summary>The non-secret part of a chat configuration (the keys of <c>SettingsJson</c>). The bot token or webhook address is the secret and is separate.</summary>
public sealed record BotSettings(
    string? DefaultChatId, TelegramParseMode ParseMode, string? DefaultUsername, string? AvatarUrl, string? DefaultChannel, string? IconEmoji);

/// <summary>
/// Reads and checks the settings of a chat configuration: <c>defaultChatId, parseMode (PlainText | MarkdownV2)</c> for Telegram,
/// <c>defaultUsername, avatarUrl</c> for Discord and <c>defaultChannel, iconEmoji</c> for Slack. Nothing here is required: the recipient
/// can come with each message.
/// </summary>
public static partial class BotSettingsReader
{
    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z0-9_]{5,32})$")]
    private static partial Regex TelegramChatId();

    // A channel name or id, with an optional # or @ in front: "alerts", "#alerts", "@someone", "C0123456789".
    [GeneratedRegex(@"^[#@]?[A-Za-z0-9._-]{1,80}$")]
    private static partial Regex SlackChannel();

    [GeneratedRegex(@"^:[A-Za-z0-9_+-]{1,50}:$")]
    private static partial Regex Emoji();

    public static BotSettings Read(JsonElement settings)
    {
        string? Text(string name) =>
            settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;

        var parseMode = Enum.TryParse<TelegramParseMode>(Text("parseMode"), ignoreCase: true, out var parsed) ? parsed : TelegramParseMode.PlainText;
        return new BotSettings(Text("defaultChatId"), parseMode, Text("defaultUsername"), Text("avatarUrl"), Text("defaultChannel"), Text("iconEmoji"));
    }

    /// <summary>The problems with these settings; empty when they are fine.</summary>
    public static IReadOnlyList<string> Validate(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object) return ["The settings must be a JSON object."];

        var problems = new List<string>();
        foreach (var name in new[] { "defaultChatId", "parseMode", "defaultUsername", "avatarUrl", "defaultChannel", "iconEmoji" })
        {
            if (settings.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                problems.Add($"{name} must be text.");
        }

        if (problems.Count > 0) return problems;

        if (settings.TryGetProperty("parseMode", out var mode) && mode.ValueKind == JsonValueKind.String
            && !Enum.TryParse<TelegramParseMode>(mode.GetString(), ignoreCase: true, out _))
            problems.Add("parseMode must be PlainText or MarkdownV2.");

        var read = Read(settings);
        if (read.DefaultChatId is { Length: > 0 } chat && !TelegramChatId().IsMatch(chat)) problems.Add("defaultChatId must be a chat id (digits, may start with -) or @channelusername.");
        if (read.DefaultUsername is { Length: > 0 } user && (user.Length > 80 || user.Any(char.IsControl))) problems.Add("defaultUsername must be at most 80 characters, on one line.");
        if (read.AvatarUrl is { Length: > 0 } avatar && !IsPlainHttpsUrl(avatar)) problems.Add("avatarUrl must be an https address.");
        if (read.DefaultChannel is { Length: > 0 } channel && !SlackChannel().IsMatch(channel)) problems.Add("defaultChannel must be a channel name or id, such as #alerts.");
        if (read.IconEmoji is { Length: > 0 } icon && !Emoji().IsMatch(icon)) problems.Add("iconEmoji must look like :warning:.");
        return problems;
    }

    public static bool IsTelegramChatId(string? value) => value is not null && TelegramChatId().IsMatch(value);

    public static bool IsSlackChannel(string? value) => value is not null && SlackChannel().IsMatch(value);

    private static bool IsPlainHttpsUrl(string value) =>
        value.Length <= 500 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
}

/// <summary>
/// Checks the secret of a chat configuration. For Discord and Slack the secret is the webhook address, which is also an outbound request
/// target, so it must be an https address on the platform's own host with the platform's own path: a configuration cannot make the service
/// call an arbitrary server (and send it the message). The Telegram bot token ends up in a URL path, so it may hold no character that could
/// change that path. Messages never repeat the value.
/// </summary>
public static partial class BotSecretRules
{
    private static readonly string[] DiscordHosts = ["discord.com", "discordapp.com"];

    [GeneratedRegex(@"^/api(/v\d+)?/webhooks/\d{5,25}/[A-Za-z0-9._-]{20,}/?$")]
    private static partial Regex DiscordPath();

    [GeneratedRegex(@"^/services/[A-Za-z0-9]+/[A-Za-z0-9]+/[A-Za-z0-9]+/?$")]
    private static partial Regex SlackPath();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{3,40}:[A-Za-z0-9_-]{3,160}$")]
    private static partial Regex TelegramToken();

    /// <summary>Null when the secret is acceptable for the provider, otherwise what is wrong (without the secret in it).</summary>
    public static string? Check(ProviderType provider, string secret) => provider switch
    {
        ProviderType.TelegramBot => TelegramToken().IsMatch(secret) ? null : "The bot token does not look like a Telegram bot token (digits, a colon, then letters and digits).",
        ProviderType.DiscordWebhook => TryParseWebhook(provider, secret, out _) ? null : "The webhook address must be an https://discord.com/api/webhooks/... address.",
        ProviderType.SlackWebhook => TryParseWebhook(provider, secret, out _) ? null : "The webhook address must be an https://hooks.slack.com/services/... address.",
        _ => null,
    };

    /// <summary>Parses a Discord or Slack webhook address and accepts it only if it is on the platform's host, with its path, over https and with no login, port, query or fragment.</summary>
    public static bool TryParseWebhook(ProviderType provider, string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 600 || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo) || !parsed.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) return false;

        var host = parsed.IdnHost.ToLowerInvariant();
        var path = parsed.AbsolutePath;
        var ok = provider switch
        {
            ProviderType.DiscordWebhook => DiscordHosts.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal)) && DiscordPath().IsMatch(path),
            ProviderType.SlackWebhook => host == "hooks.slack.com" && SlackPath().IsMatch(path),
            _ => false,
        };
        if (ok) uri = parsed;
        return ok;
    }
}

/// <summary>Neutralises what each platform would otherwise read as markup, so a text sent as plain text arrives as written.</summary>
public static class BotMarkup
{
    // Telegram MarkdownV2: these must all be escaped with a backslash anywhere in the text, or the API answers 400 "can't parse entities".
    private const string TelegramSpecials = "_*[]()~`>#+-=|{}.!\\";

    /// <summary>Escapes every character MarkdownV2 gives a meaning to.</summary>
    public static string EscapeTelegramMarkdownV2(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (TelegramSpecials.Contains(c)) escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>Slack reads &amp;, &lt; and &gt; as the start of links and mentions (&lt;!channel&gt;); these three are always escaped.</summary>
    public static string EscapeSlack(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private const string DiscordSpecials = "\\*_~`|>#";

    /// <summary>Escapes Discord markdown. (Mentions are switched off separately, in the payload.)</summary>
    public static string EscapeDiscord(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (DiscordSpecials.Contains(c)) escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
