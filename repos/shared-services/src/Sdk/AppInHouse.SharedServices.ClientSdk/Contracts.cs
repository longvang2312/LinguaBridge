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

/// <summary>
/// Send one email. Give a <see cref="Template"/> (<c>EmailVerification</c>, <c>PasswordReset</c>, <c>SystemNotification</c>) with its
/// <see cref="Variables"/>, or your own <see cref="Subject"/> and body (<c>{{variables}}</c> in them are replaced too). The project
/// defaults to the project of the API key.
/// </summary>
public sealed record SendEmailRequest(
    string To, string? Template = null, Dictionary<string, string>? Variables = null,
    string? Subject = null, string? HtmlBody = null, string? TextBody = null, string? ProjectCode = null);

public sealed record SendEmailResponse(
    bool Success, string Status, Guid? DeliveryLogId, int LatencyMs, string? ConfigurationProject, int? ConfigurationVersion);

/// <summary>
/// Test an email configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated.
/// Without it, <see cref="ProviderType"/>, <see cref="Settings"/> and <see cref="Secret"/> are tried from memory: nothing is saved or
/// activated. <see cref="Secret"/> is write-only.
/// </summary>
public sealed record SendTestEmailRequest(
    string TestRecipient, Guid? ConfigurationId = null, string? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

/// <summary>The outcome of a test. A failed test is an answer (<see cref="Success"/> false with a short, safe <see cref="Message"/>), not an exception.</summary>
public sealed record SendTestEmailResponse(
    bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

/// <summary>One delivery attempt: who it was for, whether it went out, the provider's short answer and the time. Never the subject, body or a credential.</summary>
public sealed record DeliveryLogDto(
    Guid Id, string ProjectCode, string ChannelType, string Recipient, string Status, string? ProviderResponse, int? LatencyMs, DateTimeOffset CreatedAt);

/// <summary>The API refused or failed a call. <see cref="Errors"/> holds field-level messages for a 400.</summary>
public sealed class SharedServicesApiException(int statusCode, string message, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}
