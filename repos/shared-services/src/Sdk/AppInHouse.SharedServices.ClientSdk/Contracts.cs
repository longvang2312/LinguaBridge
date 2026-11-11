using System.Text.Json;

namespace AppInHouse.SharedServices.ClientSdk;

/// <summary>
/// A channel configuration as the API returns it. The secret is never part of an answer: <see cref="HasSecret"/> says whether one is
/// stored and <see cref="MaskedSecret"/> is the placeholder to show in its place.
/// </summary>
public sealed record ChannelConfigurationResponse(
    Guid Id,
    string ProjectCode,
    string ChannelType,
    string ProviderType,
    string Name,
    int Version,
    string Status,
    JsonElement Settings,
    bool HasSecret,
    string MaskedSecret,
    string LastTestStatus,
    DateTimeOffset? LastTestedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Creates version 1 of a configuration, or the next version of an existing one (project, channel and name identify it).
/// <see cref="Secret"/> is write-only: leave it null to keep the stored secret.
/// </summary>
public sealed record SaveChannelConfigurationRequest(
    string ProjectCode, string ChannelType, string ProviderType, string Name, JsonElement? Settings = null, string? Secret = null);

public sealed record RollbackConfigurationRequest(string ProjectCode, string ChannelType, string Name, int? TargetVersion = null);

/// <summary>The API refused or failed a call. <see cref="Errors"/> holds field-level messages for a 400.</summary>
public sealed class SharedServicesApiException(int statusCode, string message, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}
