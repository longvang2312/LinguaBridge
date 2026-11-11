using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Chat;

/// <summary>How the text is meant to be read. <see cref="MarkdownV2"/> is Telegram's own dialect; the others adapt it to what each platform understands.</summary>
public enum ChatFormat
{
    /// <summary>The text is shown exactly as written: whatever a platform would treat as markup is neutralised.</summary>
    PlainText,

    /// <summary>The caller wrote the platform's markdown and wants it rendered.</summary>
    Markdown,

    /// <summary>Telegram MarkdownV2, passed through as written (the caller is responsible for its escaping).</summary>
    MarkdownV2,
}

/// <summary>
/// One message, ready to send. <see cref="Recipient"/> is a chat id for Telegram, an optional channel override for Slack, and unused by
/// Discord (a webhook always posts to the channel it was made for). Empty means "use the configuration's default".
/// <see cref="Format"/> null means "as the configuration says" (Telegram's <c>parseMode</c> setting, otherwise plain text); a format given
/// explicitly always wins, and <see cref="ChatFormat.PlainText"/> is always shown as written.
/// Metadata keys the providers know: <c>safeEscape=true</c> escapes the text whatever the format.
/// </summary>
public sealed record ChatMessage(
    string? Recipient, string Text, ChatFormat? Format, string ProjectCode, IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// What a provider reports about one send. <see cref="Target"/> says where it went in words that are safe to store (a chat id, a channel
/// name), never a webhook address or a token.
/// </summary>
public sealed record ChatSendResult(bool Success, string? ProviderMessageId = null, string? Error = null, string? ProviderResponse = null, string? Target = null);

public sealed record ChatTestResult(bool Success, string Message, int LatencyMs);

/// <summary>
/// One way of sending chat messages (Telegram Bot API, a Discord or Slack webhook, a mock). It is handed the configuration to use on every
/// call, so activating a new version or rolling back takes effect at once. A provider reports failures in the result, with a message that
/// contains neither the token nor the webhook address.
/// </summary>
public interface IChatProvider
{
    ProviderType Type { get; }

    Task<ChatSendResult> SendMessageAsync(ChatMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default);

    /// <summary>Sends <paramref name="testMessage"/> to <paramref name="testRecipient"/> (empty: the configuration's default) with this configuration.</summary>
    Task<ChatTestResult> TestConnectionAsync(ChannelRuntimeConfiguration config, string testRecipient, string testMessage, CancellationToken ct = default);
}

/// <summary>Send one chat message for a project. Empty <see cref="Recipient"/> and null <see cref="Format"/> mean the configuration's defaults.</summary>
public sealed record SendChatCommand(
    string ProjectCode, string Text, string? Recipient = null, ChatFormat? Format = null, IReadOnlyDictionary<string, string>? Metadata = null);

public enum ChatFailure
{
    None,

    /// <summary>The project has no active chat configuration, and neither has "default".</summary>
    NoConfiguration,

    /// <summary>The provider could not deliver (refused, unreachable, wrong token, not available).</summary>
    ProviderFailure,
}

public sealed record SendChatResult(
    bool Success, ChatFailure Failure, Guid? DeliveryLogId, int LatencyMs, string? Error,
    string? ConfigurationProject = null, int? ConfigurationVersion = null);

/// <summary>
/// Test a chat configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated.
/// Without it, <see cref="ProviderType"/>, <see cref="SettingsJson"/> and <see cref="Secret"/> are tried from memory: nothing is stored,
/// nothing is activated. <see cref="TestRecipient"/> and <see cref="TestMessage"/> are optional (the default chat, a stock sentence).
/// </summary>
public sealed record TestChatCommand(
    string? TestRecipient = null, string? TestMessage = null, Guid? ConfigurationId = null,
    ProviderType? ProviderType = null, string? SettingsJson = null, string? Secret = null);

public sealed record TestChatResult(bool Success, string Message, int LatencyMs, ProviderType ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

public interface IChatDispatcher
{
    Task<SendChatResult> SendAsync(SendChatCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeliveryLogDto>> RecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default);
}

public interface IChatTestService
{
    Task<TestChatResult> TestAsync(TestChatCommand command, CancellationToken cancellationToken = default);
}
