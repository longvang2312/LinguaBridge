using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.Application.Notifications;

/// <summary>
/// Send a notification built from a template. <see cref="Data"/> fills the <c>{{placeholders}}</c>. Email needs <see cref="RecipientEmail"/>;
/// <see cref="RecipientChatId"/> is optional (without it the chat configuration's default chat is used). With <see cref="Queue"/> the
/// messages are not sent now but put in the email and chat queues, which retry when a provider is unavailable.
/// </summary>
public sealed record DispatchNotificationCommand(
    string ProjectCode, string TemplateCode, IReadOnlyDictionary<string, string>? Data = null,
    string? RecipientEmail = null, string? RecipientChatId = null, NotificationPriority? Priority = null, bool Queue = false,
    bool CanSendEmail = true, bool CanSendChat = true);

public sealed record NotificationChannelResult(string Channel, bool Success, string? Error, Guid? DeliveryLogId, bool IsFallback);

/// <summary>Status: Sent (every channel asked for delivered), Partial (one of two did), Fallback (the first channel failed and the other one delivered a summary), Failed, or Queued.</summary>
public sealed record DispatchNotificationResult(
    bool Success, string Status, IReadOnlyList<NotificationChannelResult> Channels, int LatencyMs, Guid LogId, IReadOnlyList<Guid> JobIds);

public sealed record NotificationTemplateDto(
    string Code, string Name, string SubjectTemplate, string BodyTemplate, string DefaultChannel, string Priority, bool IsActive,
    IReadOnlyList<string> Placeholders);

public sealed record NotificationLogDto(
    Guid Id, string ProjectCode, string TemplateCode, string Channels, string Status, string? Recipient, int LatencyMs, DateTimeOffset CreatedAt);

public interface INotificationDispatcher
{
    Task<DispatchNotificationResult> DispatchAsync(DispatchNotificationCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationLogDto>> ListLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default);
}

/// <summary>What one channel did. <see cref="Quota"/> is set when the project's daily quota stopped it.</summary>
public sealed record ChannelSendOutcome(bool Success, string? Error, Guid? DeliveryLogId, QuotaExceededException? Quota = null);

/// <summary>Sends one message through one channel. Separate from the dispatcher so the two channels can run side by side (each in its own scope) and so a test can stand in for them.</summary>
public interface INotificationChannelSender
{
    Task<ChannelSendOutcome> SendEmailAsync(string projectCode, string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken);

    Task<ChannelSendOutcome> SendChatAsync(string projectCode, string? recipient, string text, CancellationToken cancellationToken);
}

/// <summary>
/// Sends through <see cref="IEmailDispatcher"/> and <see cref="INotificationBotDispatcher"/>, each call in a scope of its own: both save to the
/// database, and one database context must not be used by two requests at once.
/// </summary>
public sealed class ScopedNotificationChannelSender(IServiceScopeFactory scopes) : INotificationChannelSender
{
    public async Task<ChannelSendOutcome> SendEmailAsync(string projectCode, string to, string subject, string htmlBody, string textBody, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<IEmailDispatcher>()
                .SendAsync(new SendEmailCommand(projectCode, to, Subject: subject, HtmlBody: htmlBody, TextBody: textBody), cancellationToken);
            return new ChannelSendOutcome(result.Failure == EmailFailure.None, result.Error, result.DeliveryLogId);
        }
        catch (QuotaExceededException quota)
        {
            return new ChannelSendOutcome(false, quota.Message, null, quota);
        }
    }

    public async Task<ChannelSendOutcome> SendChatAsync(string projectCode, string? recipient, string text, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<INotificationBotDispatcher>()
                .SendAsync(new SendBotMessageCommand(projectCode, text, recipient, BotFormat.PlainText), cancellationToken);
            return new ChannelSendOutcome(result.Failure == BotFailure.None, result.Error, result.DeliveryLogId);
        }
        catch (QuotaExceededException quota)
        {
            return new ChannelSendOutcome(false, quota.Message, null, quota);
        }
    }
}

/// <summary>
/// The notification service: finds the template, fills its placeholders, and sends the result by email, by chat or by both (side by side).
/// A High or Critical notification whose only channel fails is tried on the other channel; for an email that failed the other channel gets a
/// short summary (the subject and the reason, never the body, which may carry a one-time link), for a chat message that failed the
/// intended recipient gets the full email. Every dispatch is recorded in the notification log.
/// </summary>
public sealed partial class NotificationDispatcher(
    INotificationRepository notifications,
    INotificationChannelSender sender,
    IJobQueue jobs,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : INotificationDispatcher
{
    public const int MaxDataEntries = 50;
    public const int MaxDataValueLength = 2000;
    private const int MaxLogs = 100;

    public async Task<DispatchNotificationResult> DispatchAsync(DispatchNotificationCommand command, CancellationToken cancellationToken = default)
    {
        var project = ChannelConfigurationValidator.NormalizeProjectCode(command.ProjectCode);
        var errors = new Dictionary<string, string[]>();
        if (!ChannelConfigurationValidator.IsValidProjectCode(project)) errors["ProjectCode"] = ["The project is required."];
        if (!NotificationTemplate.IsValidCode(command.TemplateCode)) errors["TemplateCode"] = ["Give the code of a template."];
        if (command.RecipientEmail is { Length: > 0 } email && !EmailSettingsReader.IsAddress(email)) errors["RecipientEmail"] = ["Give one valid email address."];
        if (command.RecipientChatId is { Length: > 200 }) errors["RecipientChatId"] = ["The chat id is at most 200 characters."];
        if (command.Priority is { } wanted && !Enum.IsDefined(wanted)) errors["Priority"] = ["Unknown priority."];
        var data = command.Data ?? new Dictionary<string, string>();
        if (data.Count > MaxDataEntries) errors["Data"] = [$"At most {MaxDataEntries} values."];
        else if (data.Any(d => d.Value is { Length: > MaxDataValueLength })) errors["Data"] = [$"A value is longer than {MaxDataValueLength} characters."];
        if (errors.Count > 0) throw new ValidationException(errors);

        var template = await notifications.GetTemplateAsync(command.TemplateCode, cancellationToken)
                       ?? throw new NotFoundException($"There is no notification template '{command.TemplateCode}'.");
        if (!template.IsActive) throw new DomainException($"The notification template '{template.Code}' is switched off.");

        var channel = template.DefaultChannel;
        var wantEmail = channel is NotificationChannel.Email or NotificationChannel.Both;
        var wantChat = channel is NotificationChannel.Chat or NotificationChannel.Both;
        // The caller needs the scope of every channel the template really uses (the endpoint only checked that it has some sending scope).
        if (wantEmail && !command.CanSendEmail) throw new ChannelNotPermittedException("email", template.Code);
        if (wantChat && !command.CanSendChat) throw new ChannelNotPermittedException("chat", template.Code);
        var recipientEmail = string.IsNullOrWhiteSpace(command.RecipientEmail) ? null : command.RecipientEmail.Trim();
        var recipientChat = string.IsNullOrWhiteSpace(command.RecipientChatId) ? null : command.RecipientChatId.Trim();
        if (wantEmail && recipientEmail is null) throw new ValidationException(new Dictionary<string, string[]> { ["RecipientEmail"] = [$"The template '{template.Code}' sends email: give the recipient's address."] });

        var message = Render(template, data);
        var priority = command.Priority ?? template.Priority;
        var primaryNames = string.Join('+', new[] { wantEmail ? "Email" : null, wantChat ? "Chat" : null }.Where(n => n is not null));

        if (command.Queue) return await QueueAsync(project, template, message, wantEmail ? recipientEmail : null, wantChat, recipientChat, primaryNames, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var attempts = new List<Task<ChannelSendOutcome>>();
        var names = new List<string>();
        if (wantEmail)
        {
            names.Add("Email");
            attempts.Add(sender.SendEmailAsync(project, recipientEmail!, message.Subject, message.Html, message.Text, cancellationToken));
        }

        if (wantChat)
        {
            names.Add("Chat");
            attempts.Add(sender.SendChatAsync(project, recipientChat, message.ChatText, cancellationToken));
        }

        var outcomes = await Task.WhenAll(attempts);
        var results = outcomes.Select((o, i) => new NotificationChannelResult(names[i], o.Success, o.Error is null ? null : ErrorText.Clean(o.Error), o.DeliveryLogId, false)).ToList();
        var allOutcomes = outcomes.ToList();
        var channelsText = primaryNames;

        // Fallback: one channel was asked for, it failed, and the notification matters.
        if (outcomes.Length == 1 && !outcomes[0].Success && outcomes[0].Quota is null && priority >= NotificationPriority.High)
        {
            var failedOn = names[0];
            var fallbackName = failedOn == "Email" ? "Chat" : "Email";
            ChannelSendOutcome? fallback = null;
            if (failedOn == "Email" && command.CanSendChat)
            {
                var summary = $"[{priority}] {message.Subject}\nThe email for '{template.Code}' could not be delivered: {ErrorText.Clean(outcomes[0].Error)}";
                fallback = await sender.SendChatAsync(project, recipientChat, summary, cancellationToken);
            }
            else if (failedOn == "Chat" && recipientEmail is not null && command.CanSendEmail)
            {
                fallback = await sender.SendEmailAsync(project, recipientEmail, message.Subject, message.Html, message.Text, cancellationToken);
            }

            if (fallback is not null)
            {
                allOutcomes.Add(fallback);
                results.Add(new NotificationChannelResult(fallbackName, fallback.Success, fallback.Error is null ? null : ErrorText.Clean(fallback.Error), fallback.DeliveryLogId, true));
                channelsText = $"{failedOn}→{fallbackName} (fallback)";
            }
        }

        stopwatch.Stop();
        var latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        var delivered = results.Count(r => r.Success);
        var primaryDelivered = results.Count(r => r.Success && !r.IsFallback);
        var status = primaryDelivered == outcomes.Length ? "Sent"
            : primaryDelivered > 0 ? "Partial"
            : delivered > 0 ? "Fallback"
            : "Failed";

        var log = NotificationLog.Create(project, template.Code, channelsText, status, recipientEmail ?? recipientChat, latency, clock.GetUtcNow());
        notifications.AddLog(log);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Every channel that was tried stopped on the project's quota: that is the answer (429), not a delivery failure.
        if (delivered == 0 && allOutcomes.All(o => o.Quota is not null)) throw allOutcomes[0].Quota!;

        return new DispatchNotificationResult(delivered > 0, status, results, latency, log.Id, []);
    }

    public async Task<IReadOnlyList<NotificationTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var all = await notifications.ListTemplatesAsync(cancellationToken);
        return [.. all.OrderBy(t => t.Code, StringComparer.Ordinal).Select(t => new NotificationTemplateDto(
            t.Code, t.Name, t.SubjectTemplate, t.BodyTemplate, t.DefaultChannel.ToString(), t.Priority.ToString(), t.IsActive,
            [.. EmailTemplateEngine.Placeholders(t.SubjectTemplate + "\n" + t.BodyTemplate)]))];
    }

    public async Task<IReadOnlyList<NotificationLogDto>> ListLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default)
    {
        var project = string.IsNullOrWhiteSpace(projectCode) ? null : ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        var rows = await notifications.ListLogsAsync(project, Math.Clamp(limit, 1, MaxLogs), cancellationToken);
        return [.. rows.Select(l => new NotificationLogDto(l.Id, l.ProjectCode, l.TemplateCode, l.Channels, l.Status, l.Recipient, l.LatencyMs, l.CreatedAt))];
    }

    // ------------------------------------------------------------------------------------------------ steps

    private sealed record Rendered(string Subject, string Html, string Text, string ChatText);

    /// <summary>Fills the placeholders. A placeholder with no value is an error: nobody should receive "Hello {{UserName}}" or "Hello ".</summary>
    private static Rendered Render(NotificationTemplate template, IReadOnlyDictionary<string, string> data)
    {
        var missing = new List<string>();
        var subject = LineBreaks().Replace(EmailTemplateEngine.Render(template.SubjectTemplate, data, htmlEncode: false, missing), " ").Trim();
        var text = EmailTemplateEngine.Render(template.BodyTemplate, data, htmlEncode: false, missing);
        var encoded = EmailTemplateEngine.Render(template.BodyTemplate, data, htmlEncode: true);

        if (missing.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Data"] = [$"The template '{template.Code}' needs a value for: {string.Join(", ", missing.Distinct(StringComparer.Ordinal))}."],
            });
        }

        if (subject.Length is 0 or > EmailDispatcher.MaxSubjectLength)
            throw new ValidationException(new Dictionary<string, string[]> { ["Data"] = [$"The subject must be 1 to {EmailDispatcher.MaxSubjectLength} characters once filled in."] });

        var html = "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;line-height:1.5\">"
                   + Link().Replace(encoded, "<a href=\"$0\">$0</a>").ReplaceLineEndings("<br>\n") + "</div>";
        return new Rendered(subject, html, text, subject + "\n" + text);
    }

    private async Task<DispatchNotificationResult> QueueAsync(
        string project, NotificationTemplate template, Rendered message, string? email, bool wantChat, string? chatId, string channelsText, CancellationToken cancellationToken)
    {
        var jobIds = new List<Guid>();
        if (email is not null) jobIds.Add(await jobs.EnqueueAsync(JobQueues.Email, "send", new EmailJobPayload(project, email, message.Subject, message.Html, message.Text), cancellationToken));
        if (wantChat) jobIds.Add(await jobs.EnqueueAsync(JobQueues.Chat, "send", new ChatJobPayload(project, chatId, message.ChatText), cancellationToken));

        var log = NotificationLog.Create(project, template.Code, channelsText, "Queued", email ?? chatId, 0, clock.GetUtcNow());
        notifications.AddLog(log);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DispatchNotificationResult(true, "Queued", [], 0, log.Id, jobIds);
    }

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreaks();

    // Runs on text whose values are already HTML-encoded, so a value cannot bring its own markup; only http(s) addresses become links.
    [GeneratedRegex(@"https?://[^\s<>""']+")]
    private static partial Regex Link();
}
