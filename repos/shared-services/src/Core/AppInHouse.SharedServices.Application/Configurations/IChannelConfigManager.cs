using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Configurations;

/// <summary>
/// Dynamic channel settings with a version history. Life cycle: Draft, then Active (activating archives the one before), and a
/// rollback goes back to an earlier version at once, with no deploy and no restart.
/// </summary>
public interface IChannelConfigManager
{
    /// <summary>Creates version 1 of a new configuration, or the next version of an existing one. Always a Draft.</summary>
    Task<ChannelConfigurationDto> SaveAsync(SaveChannelConfigurationCommand command, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationDto> ActivateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationDto> RollbackAsync(RollbackConfigurationCommand command, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelConfigurationDto>> ListAsync(ChannelConfigurationQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// A saved version (any status, so a draft can be tested before it is activated) with its secret decrypted, for a provider.
    /// Null when there is no such version.
    /// </summary>
    Task<ChannelRuntimeConfiguration?> GetForRuntimeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Stores the result of a "send test" on a saved version (last tested at, last result) and audits it. The settings are not touched.</summary>
    Task RecordTestResultAsync(Guid id, TestStatus result, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Active configuration with its secret decrypted, for a provider. Null when the project has none for that channel.
    /// When the project has several (different names), <paramref name="name"/> picks one; without it the most recently activated wins.
    /// </summary>
    Task<ChannelRuntimeConfiguration?> GetActiveForRuntimeAsync(
        string projectCode, ChannelType channelType, string? name = null, CancellationToken cancellationToken = default);
}
