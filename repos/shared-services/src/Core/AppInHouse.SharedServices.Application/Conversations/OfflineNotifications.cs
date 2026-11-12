using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Notifications;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Conversations;

/// <summary>
/// The job that runs after the grace period. It looks at the world again before it speaks: the member may be back online, may have read the
/// message, may have muted the conversation or left it, and if newer messages arrived meanwhile their own jobs will tell the member once
/// (so a burst of ten messages makes one notice, about the newest). The notice goes through the notification service, with the stock
/// templates, by email and/or chat depending on how the member can be reached.
/// </summary>
public sealed partial class OfflineNotificationHandler(
    IConversationRepository conversations,
    IUserPresenceTracker presence,
    INotificationDispatcher notifications,
    ILogger<OfflineNotificationHandler> logger) : IOfflineNotificationHandler
{
    public const int PreviewLength = 100;

    public async Task HandleAsync(OfflineNotificationPayload payload, CancellationToken cancellationToken = default)
    {
        var conversation = await conversations.GetAsync(payload.Project, payload.ConversationId, cancellationToken);
        var member = conversation?.FindMember(payload.RecipientId);
        if (conversation is null || member is null || member.IsMuted) return;

        var message = await conversations.GetMessageAsync(payload.MessageId, cancellationToken);
        if (message is null || message.MessageType == ChatMessageType.System) return;

        if (presence.IsOnline(payload.Project, payload.RecipientId)) return;      // back before the wait ended
        if (member.LastReadAt >= message.CreatedAt) return;                       // already read (on another device, or after coming back)

        var newest = await conversations.GetLatestUserMessageAsync(conversation.Id, cancellationToken);
        if (newest is not null && newest.Id != message.Id) return;                 // a newer message has its own job

        var data = new Dictionary<string, string>
        {
            ["SenderName"] = message.SenderName,
            ["Preview"] = Preview(message),
        };

        var attempts = new List<(string Template, DispatchNotificationCommand Command)>();
        if (member.Email is not null && payload.AllowEmail)
            attempts.Add((PlatformDefaults.ChatOfflineEmailTemplate, new DispatchNotificationCommand(payload.Project, PlatformDefaults.ChatOfflineEmailTemplate, data, RecipientEmail: member.Email)));
        if (member.ChatId is not null && payload.AllowBot)
            attempts.Add((PlatformDefaults.ChatOfflineChatTemplate, new DispatchNotificationCommand(payload.Project, PlatformDefaults.ChatOfflineChatTemplate, data, RecipientChatId: member.ChatId)));
        if (attempts.Count == 0) return;

        string? lastError = null;
        var delivered = 0;
        foreach (var (template, command) in attempts)
        {
            try
            {
                var result = await notifications.DispatchAsync(command, cancellationToken);
                if (result.Success) delivered++;
                else lastError = string.Join(" ", result.Channels.Select(c => $"{c.Channel}: {c.Error}"));
            }
            catch (Exception ex) when (ex is QuotaExceededException or ProjectInactiveException or ValidationException or NotFoundException or DomainException)
            {
                // Waiting does not help (no quota left, the project is switched off, the template is gone): do not try again.
                logger.LogWarning("The offline notice to {User} ({Template}) was refused: {Reason}", payload.RecipientId, template, ex.Message);
                lastError ??= ex.Message;
                if (attempts.Count == 1) throw new JobPermanentFailureException(ex.Message);
            }
        }

        // One way worked: done (a retry would send the working one twice). None did: let the job engine try again.
        if (delivered == 0) throw new InvalidOperationException(lastError ?? "The notification could not be delivered.");
    }

    /// <summary>
    /// A short single line of the message. Addresses are removed: the notice is built by us, not by the sender, and must not become a
    /// clickable link to wherever the sender wants (the full text is in the conversation).
    /// </summary>
    public static string Preview(ChatMessage message)
    {
        var text = Spaces().Replace(Links().Replace(message.Content, "[liên kết]"), " ").Trim();
        if (text.Length == 0) return message.AttachmentFileId is null ? "…" : "[tệp đính kèm]";
        if (text.Length <= PreviewLength) return text;
        return text[..PreviewLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Links();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

public sealed class ChatMonitor(IConversationRepository conversations, IUserPresenceTracker presence, TimeProvider clock) : IChatMonitor
{
    private const int RecentListed = 20;

    public async Task<ChatMonitorDto> GetAsync(string? projectCode, CancellationToken cancellationToken = default)
    {
        var (from, to) = QuotaGuard.Today(clock.GetUtcNow());
        var totals = await conversations.GetTotalsAsync(projectCode, from, to, cancellationToken);
        var recent = await conversations.ListRecentAsync(projectCode, RecentListed, cancellationToken);
        var online = presence.OnlineUsers(projectCode);
        return new ChatMonitorDto(
            projectCode, totals.Conversations, totals.Direct, totals.Groups, totals.MessagesInRange, online.Count, presence.ConnectionCount(projectCode), online, recent);
    }
}
