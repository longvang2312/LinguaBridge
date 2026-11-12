using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Email;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Infrastructure.Notifications.Bots;

/// <summary>
/// Where the bot providers (Telegram, Discord, Slack) get their host settings from. None of it is stored per project. The section is still
/// called <c>Chat</c> (<c>Chat__TelegramBaseUrl</c> in the environment): renaming it would silently drop the settings of a host that is already deployed.
/// </summary>
public sealed class BotOptions
{
    public const string SectionName = "Chat";

    /// <summary>
    /// The Telegram Bot API address. A host setting, deliberately not a per-configuration one: the bot token travels in the request path,
    /// so a configuration that could name the server could collect tokens.
    /// </summary>
    public string TelegramBaseUrl { get; set; } = "https://api.telegram.org";

    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>The part every chat provider shares: "send test" as a timed send of a short message.</summary>
public abstract class BotProviderBase : IBotProvider
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public abstract ProviderType Type { get; }

    public abstract Task<BotSendResult> SendMessageAsync(BotMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default);

    public async Task<BotTestResult> TestConnectionAsync(ChannelRuntimeConfiguration config, string testRecipient, string testMessage, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await SendMessageAsync(
            new BotMessage(string.IsNullOrWhiteSpace(testRecipient) ? null : testRecipient, testMessage, BotFormat.PlainText, config.ProjectCode), config, ct);
        stopwatch.Stop();

        var latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        return result.Success
            ? new BotTestResult(true, $"Test message accepted by the {Type} provider.", latency)
            : new BotTestResult(false, ErrorText.Clean(result.Error, config.Secret), latency);
    }

    /// <summary>True for the explicit request to neutralise markup whatever the format (<c>safeEscape=true</c> in the metadata).</summary>
    protected static bool SafeEscape(BotMessage message) =>
        message.Metadata is { } metadata && metadata.TryGetValue("safeEscape", out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps secrets out of a message: the secret itself, and for a webhook each part of its address (the token is the part that matters).</summary>
    protected static string[] Secrets(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return [];
        var parts = Uri.TryCreate(secret, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            : secret.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return [secret, .. parts.Where(p => p.Length >= 8)];
    }
}

/// <summary>For development and tests with no internet: keeps what would have been sent in memory (<see cref="SentMessages"/>). The text is not logged.</summary>
public sealed class MockBotProvider(ILogger<MockBotProvider> logger) : BotProviderBase
{
    private const int MaxKept = 1000;
    private readonly ConcurrentQueue<BotMessage> _sent = new();

    public override ProviderType Type => ProviderType.Mock;

    /// <summary>The last messages "sent" (at most 1000, oldest dropped first).</summary>
    public IReadOnlyList<BotMessage> SentMessages => [.. _sent];

    public void Clear() => _sent.Clear();

    public override Task<BotSendResult> SendMessageAsync(BotMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default)
    {
        _sent.Enqueue(message);
        while (_sent.Count > MaxKept) _sent.TryDequeue(out _);

        logger.LogInformation("Mock chat message to {Recipient} (project {Project}), {Length} characters", message.Recipient ?? "(default)", config.ProjectCode, message.Text.Length);
        return Task.FromResult(new BotSendResult(true, ProviderMessageId: $"mock-{Guid.NewGuid():N}", ProviderResponse: "Stored by the mock provider", Target: message.Recipient));
    }
}

/// <summary>Telegram Bot API (<c>sendMessage</c>). The bot token is the configuration's decrypted secret; it lives only in the request address, never in a message or a log.</summary>
public sealed class TelegramBotProvider(HttpClient http, ILogger<TelegramBotProvider> logger) : BotProviderBase
{
    public override ProviderType Type => ProviderType.TelegramBot;

    public override async Task<BotSendResult> SendMessageAsync(BotMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default)
    {
        var token = config.Secret;
        if (string.IsNullOrWhiteSpace(token) || BotSecretRules.Check(ProviderType.TelegramBot, token) is not null)
            return new BotSendResult(false, Error: "The Telegram bot token is missing or not valid.");

        var settings = BotSettingsReader.Read(config.Settings);
        var chatId = string.IsNullOrWhiteSpace(message.Recipient) ? settings.DefaultChatId : message.Recipient.Trim();
        if (string.IsNullOrWhiteSpace(chatId)) return new BotSendResult(false, Error: "There is no chat id: give a recipient, or set a default chat id in the configuration.");
        if (!BotSettingsReader.IsTelegramChatId(chatId)) return new BotSendResult(false, Error: "The chat id is not valid (digits, may start with -, or @channelusername).");

        var (text, parseMode) = Prepare(message, settings);
        var payload = new { chat_id = chatId, text, parse_mode = parseMode, disable_web_page_preview = true };

        // The token is part of the address: it was checked above to hold nothing that could change the path. The address is built as a
        // Uri on purpose: as a string, "bot123:abc/sendMessage" reads as an absolute address with the scheme "bot123" (the colon before the
        // first slash), and the request would never go to Telegram. The "./" keeps it a path.
        var address = new Uri(http.BaseAddress ?? new Uri("https://api.telegram.org/"), $"./bot{token}/sendMessage");
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = JsonContent.Create(payload, options: Json) };
        try
        {
            using var response = await http.SendAsync(request, ct);
            return await ReadAsync(response, chatId, token, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BotSendResult(false, Error: "Telegram did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not reach Telegram: {Reason}", ex.HttpRequestError);   // the exception text is not logged: it can carry the address
            return new BotSendResult(false, Error: "Could not reach Telegram.");
        }
    }

    /// <summary>
    /// What is sent and how Telegram should read it. Plain text is escaped completely (and sent as MarkdownV2), so every character arrives
    /// as written and none can make the API answer 400 "can't parse entities"; MarkdownV2 is the caller's own text.
    /// </summary>
    public static (string Text, string? ParseMode) Prepare(BotMessage message, BotSettings settings)
    {
        var format = message.Format ?? (settings.ParseMode == TelegramParseMode.MarkdownV2 ? BotFormat.MarkdownV2 : BotFormat.PlainText);
        if (format == BotFormat.PlainText || SafeEscape(message)) return (BotMarkup.EscapeTelegramMarkdownV2(message.Text), "MarkdownV2");
        return (message.Text, format == BotFormat.MarkdownV2 ? "MarkdownV2" : "Markdown");
    }

    private static async Task<BotSendResult> ReadAsync(HttpResponseMessage response, string chatId, string token, CancellationToken ct)
    {
        string? description = null;
        long? messageId = null;
        int? retryAfter = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString();
            if (root.TryGetProperty("result", out var result) && result.TryGetProperty("message_id", out var id) && id.TryGetInt64(out var parsed)) messageId = parsed;
            if (root.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("retry_after", out var retry) && retry.TryGetInt32(out var seconds)) retryAfter = seconds;
        }
        catch (JsonException)
        {
            // Not Telegram's JSON (a proxy's page): the status code alone has to do.
        }

        if (response.IsSuccessStatusCode && messageId is not null)
            return new BotSendResult(true, messageId.ToString(), ProviderResponse: $"Telegram accepted (message {messageId})", Target: chatId);

        var status = (int)response.StatusCode;
        var error = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Telegram rejected the bot token (401).",
            HttpStatusCode.Forbidden => $"The bot may not post to that chat (403): {description ?? "it was blocked or removed"}.",
            HttpStatusCode.TooManyRequests => $"Telegram is limiting the rate (429){(retryAfter is { } s ? $"; retry in {s} s" : string.Empty)}.",
            _ => $"Telegram answered {status}{(description is null ? "." : $": {description}")}",
        };
        return new BotSendResult(false, Error: ErrorText.Clean(error, Secrets(token)), Target: chatId);
    }
}

/// <summary>
/// Discord Incoming Webhook. The webhook address is the configuration's decrypted secret. It is checked to be a Discord address, its host
/// is checked against the outbound policy, redirects are not followed, mentions are switched off, and the text is cut to Discord's limit.
/// </summary>
public sealed class DiscordWebhookBotProvider(HttpClient http, OutboundHostPolicy hostPolicy, ILogger<DiscordWebhookBotProvider> logger) : BotProviderBase
{
    public const int MaxContentLength = 2000;

    public override ProviderType Type => ProviderType.DiscordWebhook;

    public override async Task<BotSendResult> SendMessageAsync(BotMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default)
    {
        if (!BotSecretRules.TryParseWebhook(ProviderType.DiscordWebhook, config.Secret, out var webhook))
            return new BotSendResult(false, Error: "The webhook address is missing or is not a Discord webhook address.");

        var blocked = await hostPolicy.CheckAsync(webhook.Host, ct);
        if (blocked is not null) return new BotSendResult(false, Error: blocked);

        var settings = BotSettingsReader.Read(config.Settings);
        var payload = new
        {
            content = Content(message),
            username = string.IsNullOrWhiteSpace(settings.DefaultUsername) ? null : settings.DefaultUsername,
            avatar_url = string.IsNullOrWhiteSpace(settings.AvatarUrl) ? null : settings.AvatarUrl,
            allowed_mentions = new { parse = Array.Empty<string>() },   // @everyone, @here and role mentions in the text do not ping anyone
        };

        // ?wait=true makes Discord answer with the message (and its id) instead of an empty 204.
        using var request = new HttpRequestMessage(HttpMethod.Post, new UriBuilder(webhook) { Query = "wait=true" }.Uri) { Content = JsonContent.Create(payload, options: Json) };
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                string? id = null;
                try
                {
                    using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (document.RootElement.TryGetProperty("id", out var value)) id = value.GetString();
                }
                catch (JsonException)
                {
                    // 204 or an empty answer: accepted all the same.
                }

                return new BotSendResult(true, id, ProviderResponse: $"Discord accepted ({(int)response.StatusCode})" + (id is null ? string.Empty : $" message {id}"));
            }

            return new BotSendResult(false, Error: ErrorText.Clean(Describe(response), Secrets(config.Secret)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BotSendResult(false, Error: "Discord did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not reach Discord: {Reason}", ex.HttpRequestError);
            return new BotSendResult(false, Error: "Could not reach Discord.");
        }
    }

    /// <summary>The text as Discord will show it: escaped when it is plain text, and never longer than Discord accepts.</summary>
    public static string Content(BotMessage message)
    {
        var text = message.Format is null or BotFormat.PlainText || SafeEscape(message) ? BotMarkup.EscapeDiscord(message.Text) : message.Text;
        return text.Length <= MaxContentLength ? text : text[..(MaxContentLength - 1)] + "…";
    }

    private static string Describe(HttpResponseMessage response) => (int)response.StatusCode switch
    {
        401 or 403 or 404 => $"Discord does not accept this webhook ({(int)response.StatusCode}); it may have been deleted or changed.",
        429 => "Discord is limiting the rate (429).",
        >= 300 and < 400 => "Discord answered with a redirect, which is not followed.",
        var status => $"Discord answered {status}.",
    };
}

/// <summary>
/// Slack Incoming Webhook. The webhook address is the configuration's decrypted secret. It is checked to be a Slack address, its host is
/// checked against the outbound policy, redirects are not followed, and <c>&amp; &lt; &gt;</c> in the text are always escaped (otherwise the
/// text could carry links and <c>&lt;!channel&gt;</c> mentions).
/// </summary>
public sealed class SlackWebhookBotProvider(HttpClient http, OutboundHostPolicy hostPolicy, ILogger<SlackWebhookBotProvider> logger) : BotProviderBase
{
    public override ProviderType Type => ProviderType.SlackWebhook;

    public override async Task<BotSendResult> SendMessageAsync(BotMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default)
    {
        if (!BotSecretRules.TryParseWebhook(ProviderType.SlackWebhook, config.Secret, out var webhook))
            return new BotSendResult(false, Error: "The webhook address is missing or is not a Slack webhook address.");

        var settings = BotSettingsReader.Read(config.Settings);
        var channel = string.IsNullOrWhiteSpace(message.Recipient) ? settings.DefaultChannel : message.Recipient.Trim();
        if (!string.IsNullOrWhiteSpace(channel) && !BotSettingsReader.IsSlackChannel(channel))
            return new BotSendResult(false, Error: "The channel is not valid (a channel name or id, such as #alerts).");

        var blocked = await hostPolicy.CheckAsync(webhook.Host, ct);
        if (blocked is not null) return new BotSendResult(false, Error: blocked);

        var payload = new
        {
            text = BotMarkup.EscapeSlack(message.Text),
            channel = string.IsNullOrWhiteSpace(channel) ? null : channel,
            icon_emoji = string.IsNullOrWhiteSpace(settings.IconEmoji) ? null : settings.IconEmoji,
            mrkdwn = message.Format is not (null or BotFormat.PlainText) && !SafeEscape(message),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, webhook) { Content = JsonContent.Create(payload, options: Json) };
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (response.IsSuccessStatusCode) return new BotSendResult(true, ProviderResponse: $"Slack accepted ({(int)response.StatusCode})", Target: channel);

            // Slack answers with a short code ("invalid_payload", "no_service", "channel_not_found"); it never echoes the address.
            var detail = body.Length is > 0 and <= 60 && !body.Contains('<') ? $": {body}" : ".";
            var error = response.StatusCode == HttpStatusCode.TooManyRequests ? "Slack is limiting the rate (429)." : $"Slack answered {(int)response.StatusCode}{detail}";
            return new BotSendResult(false, Error: ErrorText.Clean(error, Secrets(config.Secret)), Target: channel);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BotSendResult(false, Error: "Slack did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not reach Slack: {Reason}", ex.HttpRequestError);
            return new BotSendResult(false, Error: "Could not reach Slack.");
        }
    }
}
