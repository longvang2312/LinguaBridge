using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Email;

/// <summary>
/// One email, already rendered. The sender is never part of it: it comes from the active configuration, so a caller cannot send as
/// somebody else. <see cref="HtmlBody"/> or <see cref="TextBody"/> may be empty, not both.
/// </summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string? TextBody = null);

/// <summary>What a provider reports about one send. The dispatcher measures the time.</summary>
public sealed record EmailSendResult(bool Success, string? ProviderMessageId = null, string? Error = null, string? ProviderResponse = null);

public sealed record EmailTestResult(bool Success, string Message, int LatencyMs);

/// <summary>
/// One way of sending email (SMTP, SendGrid, a mock). It is handed the configuration to use on every call, so activating a new
/// version or rolling back takes effect without a restart. Failures are reported in the result; a provider must not put a secret
/// in <see cref="EmailSendResult.Error"/>.
/// </summary>
public interface IEmailProvider
{
    ProviderType Type { get; }

    Task<EmailSendResult> SendAsync(EmailMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default);

    /// <summary>Sends a short test email to <paramref name="testRecipient"/> with this configuration (and so proves the connection and the login).</summary>
    Task<EmailTestResult> TestConnectionAsync(ChannelRuntimeConfiguration config, string testRecipient, CancellationToken ct = default);
}

/// <summary>A row of the delivery log as a client may see it: no body, no subject, no credential.</summary>
public sealed record DeliveryLogDto(
    Guid Id, string ProjectCode, ChannelType ChannelType, string Recipient, DeliveryStatus Status,
    string? ProviderResponse, int? LatencyMs, DateTimeOffset CreatedAt);

public interface IDeliveryLogRepository
{
    /// <summary>Adds the row to the current unit of work.</summary>
    void Add(DeliveryLog log);

    Task<IReadOnlyList<DeliveryLog>> ListRecentAsync(string? projectCode, ChannelType channelType, int limit, CancellationToken cancellationToken = default);

    /// <summary>The same for several channels together (chat is saved as Chat, or as Telegram by configurations that predate Chat).</summary>
    Task<IReadOnlyList<DeliveryLog>> ListRecentAsync(string? projectCode, IReadOnlyCollection<ChannelType> channelTypes, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Send one email for a project. Give either a <see cref="Template"/> name with its <see cref="Variables"/>, or your own
/// <see cref="Subject"/> and body (<c>{{variables}}</c> in them are replaced too).
/// </summary>
public sealed record SendEmailCommand(
    string ProjectCode, string To, string? Template = null, IReadOnlyDictionary<string, string>? Variables = null,
    string? Subject = null, string? HtmlBody = null, string? TextBody = null);

public enum EmailFailure
{
    None,

    /// <summary>The project has no active email configuration, and neither has "default".</summary>
    NoConfiguration,

    /// <summary>The provider could not deliver (refused, unreachable, wrong login, not available).</summary>
    ProviderFailure,
}

public sealed record SendEmailResult(
    bool Success, EmailFailure Failure, Guid? DeliveryLogId, int LatencyMs, string? Error,
    string? ConfigurationProject = null, int? ConfigurationVersion = null);

/// <summary>
/// Test an email configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated.
/// Without it, <see cref="ProviderType"/>, <see cref="SettingsJson"/> and <see cref="Secret"/> are tested straight from memory:
/// nothing is stored, nothing is activated.
/// </summary>
public sealed record TestEmailCommand(
    string TestRecipient, Guid? ConfigurationId = null, ProviderType? ProviderType = null, string? SettingsJson = null, string? Secret = null);

public sealed record TestEmailResult(bool Success, string Message, int LatencyMs, ProviderType ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

public interface IEmailDispatcher
{
    Task<SendEmailResult> SendAsync(SendEmailCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeliveryLogDto>> RecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default);
}

public interface IEmailTestService
{
    Task<TestEmailResult> TestAsync(TestEmailCommand command, CancellationToken cancellationToken = default);
}
