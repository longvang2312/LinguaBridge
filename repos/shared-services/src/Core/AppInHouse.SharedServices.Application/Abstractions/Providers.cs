using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Abstractions;

// What the channel services implement (email: see the Email folder; chat: the Chat folder; storage comes with its own task). A provider is given the configuration it must use
// on every call, so a newly activated version (or a rollback) takes effect without restarting the service.

public interface IStorageProvider
{
    ProviderType Type { get; }

    /// <summary>The object key is made by the server, never by the caller.</summary>
    Task PutAsync(ChannelRuntimeConfiguration configuration, string objectKey, Stream content, string mimeType, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default);
}
