using System.Diagnostics;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Chat;

/// <summary>
/// Sends a chat message for a project: picks the project's active chat configuration (or the "default" project's), routes to the provider
/// that configuration names, measures the time, and writes the outcome to the delivery log. The log keeps who it was for (a chat id, a
/// channel), whether it went out, the provider's short answer and the time; never the message, a bot token or a webhook address.
/// </summary>
public sealed class ChatDispatcher(
    IChannelConfigManager configurations,
    IEnumerable<IChatProvider> providers,
    IDeliveryLogRepository deliveryLogs,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ChatDispatcher> logger) : IChatDispatcher
{
    public const string FallbackProject = "default";

    /// <summary>The largest text a caller may send; Telegram's own limit. A provider with a smaller limit shortens the text itself.</summary>
    public const int MaxTextLength = 4096;

    private const int MaxRecentLogs = 100;
    private const int MaxMetadataEntries = 20;

    public async Task<SendChatResult> SendAsync(SendChatCommand command, CancellationToken cancellationToken = default)
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
            return await Record(project, ChannelType.Chat, message.Recipient, ChatFailure.ProviderFailure,
                "The stored chat credentials cannot be read; enter them again.", 0, null, cancellationToken);
        }

        if (configuration is null)
        {
            var error = project == FallbackProject
                ? "There is no active chat configuration."
                : $"Project '{project}' has no active chat configuration, and neither has '{FallbackProject}'.";
            return await Record(project, ChannelType.Chat, message.Recipient, ChatFailure.NoConfiguration, error, 0, null, cancellationToken);
        }

        var provider = providers.FirstOrDefault(p => p.Type == configuration.ProviderType);
        if (provider is null)
        {
            return await Record(project, configuration.ChannelType, message.Recipient, ChatFailure.ProviderFailure,
                $"The {configuration.ProviderType} chat provider is not available.", 0, configuration, cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        ChatSendResult result;
        try
        {
            result = await provider.SendMessageAsync(message, configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {Provider} chat provider threw while sending for project {Project}.", configuration.ProviderType, project);
            result = new ChatSendResult(false, Error: ErrorText.Clean(ex.Message, configuration.Secret));
        }

        stopwatch.Stop();
        var latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        var target = result.Target ?? message.Recipient;
        return result.Success
            ? await Record(project, configuration.ChannelType, target, ChatFailure.None,
                ErrorText.Clean(result.ProviderResponse ?? result.ProviderMessageId ?? "Accepted", configuration.Secret), latency, configuration, cancellationToken)
            : await Record(project, configuration.ChannelType, target, ChatFailure.ProviderFailure,
                ErrorText.Clean(result.Error, configuration.Secret), latency, configuration, cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryLogDto>> RecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default)
    {
        var project = string.IsNullOrWhiteSpace(projectCode) ? null : ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        var rows = await deliveryLogs.ListRecentAsync(project, ProviderCatalog.ChatChannels, Math.Clamp(limit, 1, MaxRecentLogs), cancellationToken);
        return [.. rows.Select(l => new DeliveryLogDto(l.Id, l.ProjectCode, l.ChannelType, l.Recipient, l.Status, l.ProviderResponse, l.LatencyMs, l.CreatedAt))];
    }

    // ------------------------------------------------------------------------------------------------ steps

    /// <summary>Checks the request. Anything wrong with it is a ValidationException (nothing is sent, nothing is logged).</summary>
    private static ChatMessage Prepare(SendChatCommand command, string project)
    {
        var errors = new Dictionary<string, string[]>();
        if (project.Length == 0) errors["ProjectCode"] = ["The project is required."];
        if (string.IsNullOrWhiteSpace(command.Text)) errors["Text"] = ["The message is required."];
        else if (command.Text.Length > MaxTextLength) errors["Text"] = [$"The message is longer than {MaxTextLength} characters."];
        if (command.Format is { } format && !Enum.IsDefined(format)) errors["Format"] = ["Use PlainText, Markdown or MarkdownV2."];

        var recipient = string.IsNullOrWhiteSpace(command.Recipient) ? null : command.Recipient.Trim();
        if (recipient is { Length: > 100 } || recipient is not null && recipient.Any(char.IsControl))
            errors["Recipient"] = ["The recipient must be at most 100 characters, on one line."];

        if (command.Metadata is { } metadata
            && (metadata.Count > MaxMetadataEntries || metadata.Any(m => m.Key.Length is 0 or > 50 || m.Value?.Length > 500)))
            errors["Metadata"] = [$"At most {MaxMetadataEntries} entries, keys up to 50 characters, values up to 500."];

        if (errors.Count > 0) throw new ValidationException(errors);
        return new ChatMessage(recipient, command.Text, command.Format, project, command.Metadata);
    }

    /// <summary>The project's own configuration first, then the default project's; within each, a chat configuration, then one saved under the older Telegram channel.</summary>
    private async Task<ChannelRuntimeConfiguration?> Resolve(string project, CancellationToken cancellationToken)
    {
        foreach (var owner in project == FallbackProject ? new[] { FallbackProject } : [project, FallbackProject])
        {
            foreach (var channel in ProviderCatalog.ChatChannels)
            {
                var found = await configurations.GetActiveForRuntimeAsync(owner, channel, cancellationToken: cancellationToken);
                if (found is not null) return found;
            }
        }

        return null;
    }

    private async Task<SendChatResult> Record(
        string project, ChannelType channel, string? target, ChatFailure failure, string text, int latencyMs,
        ChannelRuntimeConfiguration? configuration, CancellationToken cancellationToken)
    {
        var log = DeliveryLog.Create(
            project, channel, string.IsNullOrWhiteSpace(target) ? "(default)" : target,
            failure == ChatFailure.None ? DeliveryStatus.Sent : DeliveryStatus.Failed,
            text.Length > 500 ? text[..500] : text, latencyMs, clock.GetUtcNow());
        deliveryLogs.Add(log);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new SendChatResult(
            failure == ChatFailure.None, failure, log.Id, latencyMs, failure == ChatFailure.None ? null : text,
            configuration?.ProjectCode, configuration?.Version);
    }
}
