using System.Text.Json;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Configurations;

/// <summary>
/// Saves a configuration: the first call for a project, channel and name creates version 1, every later call the next version
/// (always a Draft). <see cref="Secret"/> is write-only: leave it empty (or send back the mask) to keep the stored secret.
/// </summary>
public sealed record SaveChannelConfigurationCommand(
    string ProjectCode, ChannelType ChannelType, ProviderType ProviderType, string Name, string SettingsJson, string? Secret = null);

public sealed record RollbackConfigurationCommand(string ProjectCode, ChannelType ChannelType, string Name, int? TargetVersion = null);

public sealed record ChannelConfigurationQuery(string? ProjectCode = null, ChannelType? ChannelType = null, ConfigurationStatus? Status = null);

/// <summary>
/// A configuration as a client may see it. There is deliberately no property that can hold the secret: only
/// <see cref="HasSecret"/> and the <see cref="MaskedSecret"/> placeholder.
/// </summary>
public sealed record ChannelConfigurationDto(
    Guid Id,
    string ProjectCode,
    ChannelType ChannelType,
    ProviderType ProviderType,
    string Name,
    int Version,
    ConfigurationStatus Status,
    JsonElement Settings,
    bool HasSecret,
    string MaskedSecret,
    TestStatus LastTestStatus,
    DateTimeOffset? LastTestedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The active configuration with its secret in plain text, for the provider that is about to use it. Only ever handed to code
/// inside the service; never serialize it into a response or a log.
/// </summary>
public sealed class ChannelRuntimeConfiguration
{
    public required Guid Id { get; init; }
    public required string ProjectCode { get; init; }
    public required ChannelType ChannelType { get; init; }
    public required ProviderType ProviderType { get; init; }
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required JsonElement Settings { get; init; }
    public string? Secret { get; init; }

    public override string ToString() => $"{ProjectCode}/{ChannelType}/{Name} v{Version} ({ProviderType})";
}
