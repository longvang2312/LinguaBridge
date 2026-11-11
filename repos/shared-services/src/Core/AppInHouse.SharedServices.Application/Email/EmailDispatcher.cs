using System.Diagnostics;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Email;

/// <summary>
/// Sends an email for a project: picks the project's active email configuration (or the "default" project's), routes to the provider
/// that configuration names, measures the time, and writes the outcome to the delivery log. The log keeps who it was for, whether it
/// went out, the provider's short answer and the time; never the subject, the body or a credential.
/// </summary>
public sealed class EmailDispatcher(
    IChannelConfigManager configurations,
    IEnumerable<IEmailProvider> providers,
    EmailTemplateRenderer renderer,
    IDeliveryLogRepository deliveryLogs,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<EmailDispatcher> logger) : IEmailDispatcher
{
    public const string FallbackProject = "default";
    public const int MaxSubjectLength = 200;
    public const int MaxBodyLength = 512 * 1024;
    private const int MaxRecentLogs = 100;

    public async Task<SendEmailResult> SendAsync(SendEmailCommand command, CancellationToken cancellationToken = default)
    {
        var project = ChannelConfigurationValidator.NormalizeProjectCode(command.ProjectCode);
        var message = Prepare(command, project);

        ChannelRuntimeConfiguration? configuration;
        try
        {
            configuration = await Resolve(project, cancellationToken);
        }
        catch (SecretProtectionException)
        {
            return await Record(project, message.To, EmailFailure.ProviderFailure, "The stored email credentials cannot be read; enter them again.", 0, null, cancellationToken);
        }

        if (configuration is null)
        {
            var error = project == FallbackProject
                ? "There is no active email configuration."
                : $"Project '{project}' has no active email configuration, and neither has '{FallbackProject}'.";
            return await Record(project, message.To, EmailFailure.NoConfiguration, error, 0, null, cancellationToken);
        }

        var provider = providers.FirstOrDefault(p => p.Type == configuration.ProviderType);
        if (provider is null)
        {
            return await Record(project, message.To, EmailFailure.ProviderFailure,
                $"The {configuration.ProviderType} email provider is not available.", 0, configuration, cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        EmailSendResult result;
        try
        {
            result = await provider.SendAsync(message, configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {Provider} email provider threw while sending for project {Project}.", configuration.ProviderType, project);
            result = new EmailSendResult(false, Error: ErrorText.Clean(ex.Message, configuration.Secret));
        }

        stopwatch.Stop();
        var latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        return result.Success
            ? await Record(project, message.To, EmailFailure.None, ErrorText.Clean(result.ProviderResponse ?? result.ProviderMessageId ?? "Accepted", configuration.Secret), latency, configuration, cancellationToken)
            : await Record(project, message.To, EmailFailure.ProviderFailure, ErrorText.Clean(result.Error, configuration.Secret), latency, configuration, cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryLogDto>> RecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default)
    {
        var project = string.IsNullOrWhiteSpace(projectCode) ? null : ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        var rows = await deliveryLogs.ListRecentAsync(project, ChannelType.Email, Math.Clamp(limit, 1, MaxRecentLogs), cancellationToken);
        return [.. rows.Select(l => new DeliveryLogDto(l.Id, l.ProjectCode, l.ChannelType, l.Recipient, l.Status, l.ProviderResponse, l.LatencyMs, l.CreatedAt))];
    }

    // ------------------------------------------------------------------------------------------------ steps

    /// <summary>Checks the request and renders it. Anything wrong with the request is a ValidationException (nothing is sent, nothing is logged).</summary>
    private EmailMessage Prepare(SendEmailCommand command, string project)
    {
        var errors = new Dictionary<string, string[]>();
        if (!EmailSettingsReader.IsAddress(command.To)) errors["To"] = ["Give one valid email address."];
        if (project.Length == 0) errors["ProjectCode"] = ["The project is required."];

        var byTemplate = !string.IsNullOrWhiteSpace(command.Template);
        var custom = !string.IsNullOrWhiteSpace(command.Subject) || !string.IsNullOrWhiteSpace(command.HtmlBody) || !string.IsNullOrWhiteSpace(command.TextBody);
        if (byTemplate && custom) errors["Template"] = ["Give a template or your own subject and body, not both."];
        else if (!byTemplate && !custom) errors["Template"] = ["Give a template, or a subject and a body."];
        else if (!byTemplate && string.IsNullOrWhiteSpace(command.Subject)) errors["Subject"] = ["The subject is required."];
        else if (!byTemplate && string.IsNullOrWhiteSpace(command.HtmlBody) && string.IsNullOrWhiteSpace(command.TextBody)) errors["Body"] = ["Give an HTML body or a text body."];
        if (command.HtmlBody is { Length: > MaxBodyLength } || command.TextBody is { Length: > MaxBodyLength }) errors["Body"] = [$"The body is larger than {MaxBodyLength / 1024} KB."];

        if (errors.Count > 0) throw new ValidationException(errors);

        var rendered = byTemplate
            ? renderer.RenderTemplate(command.Template!, command.Variables)
            : renderer.RenderCustom(command.Subject!, command.HtmlBody, command.TextBody, command.Variables);

        if (rendered.Subject.Length is 0 or > MaxSubjectLength)
            throw new ValidationException(new Dictionary<string, string[]> { ["Subject"] = [$"The subject must be 1 to {MaxSubjectLength} characters."] });

        return new EmailMessage(command.To.Trim(), rendered.Subject, rendered.HtmlBody, rendered.TextBody);
    }

    private async Task<ChannelRuntimeConfiguration?> Resolve(string project, CancellationToken cancellationToken)
    {
        var own = await configurations.GetActiveForRuntimeAsync(project, ChannelType.Email, cancellationToken: cancellationToken);
        if (own is not null || project == FallbackProject) return own;
        return await configurations.GetActiveForRuntimeAsync(FallbackProject, ChannelType.Email, cancellationToken: cancellationToken);
    }

    private async Task<SendEmailResult> Record(
        string project, string recipient, EmailFailure failure, string text, int latencyMs,
        ChannelRuntimeConfiguration? configuration, CancellationToken cancellationToken)
    {
        var log = DeliveryLog.Create(
            project, ChannelType.Email, recipient, failure == EmailFailure.None ? DeliveryStatus.Sent : DeliveryStatus.Failed,
            text.Length > 500 ? text[..500] : text, latencyMs, clock.GetUtcNow());
        deliveryLogs.Add(log);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new SendEmailResult(
            failure == EmailFailure.None, failure, log.Id, latencyMs, failure == EmailFailure.None ? null : text,
            configuration?.ProjectCode, configuration?.Version);
    }
}
