using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Abstractions;

// What the channel services will implement (email, Telegram and storage tasks). A provider is given the configuration it must use
// on every call, so a newly activated version (or a rollback) takes effect without restarting the service.

public sealed record ProviderResult(bool Success, string? ProviderMessageId = null, string? Error = null, int LatencyMs = 0);

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string? TextBody = null, string? From = null);

public interface IEmailProvider
{
    ProviderType Type { get; }
    Task<ProviderResult> SendAsync(ChannelRuntimeConfiguration configuration, EmailMessage message, CancellationToken cancellationToken = default);
}

public interface ITelegramProvider
{
    ProviderType Type { get; }

    /// <summary>Sends text (MarkdownV2 escaping is the provider's job).</summary>
    Task<ProviderResult> SendMessageAsync(ChannelRuntimeConfiguration configuration, string chatId, string text, CancellationToken cancellationToken = default);
}

public interface IStorageProvider
{
    ProviderType Type { get; }

    /// <summary>The object key is made by the server, never by the caller.</summary>
    Task PutAsync(ChannelRuntimeConfiguration configuration, string objectKey, Stream content, string mimeType, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default);
}
