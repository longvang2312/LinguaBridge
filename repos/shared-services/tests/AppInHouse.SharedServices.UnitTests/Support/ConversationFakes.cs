using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Application.Notifications;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests.Support;

/// <summary>The conversation store in memory, with the same rules as the database one (project isolation, newest first, the cursor with its tie-break).</summary>
internal sealed class FakeConversationRepository : IConversationRepository
{
    public List<Conversation> Conversations { get; } = [];
    public List<ChatMessage> Messages { get; } = [];

    public void Add(Conversation conversation) => Conversations.Add(conversation);

    public void AddMessage(ChatMessage message) => Messages.Add(message);

    public void RemoveMember(ConversationMember member)
    {
        foreach (var conversation in Conversations) conversation.Members.Remove(member);
    }

    public void Discard(Conversation conversation) => Conversations.Remove(conversation);

    public Task<Conversation?> GetAsync(string projectCode, Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversations.FirstOrDefault(c => c.ProjectCode == projectCode && c.Id == id));

    public Task<Conversation?> FindDirectAsync(string projectCode, string directKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(Conversations.FirstOrDefault(c => c.ProjectCode == projectCode && c.DirectKey == directKey));

    public Task<IReadOnlyList<ConversationListRow>> ListForUserAsync(string projectCode, string userId, int limit, CancellationToken cancellationToken = default)
    {
        var rows = Conversations
            .Where(c => c.ProjectCode == projectCode && c.FindMember(userId) is not null)
            .OrderByDescending(c => c.LastMessageAt).Take(limit)
            .Select(c =>
            {
                var mine = c.FindMember(userId)!;
                var inConversation = Messages.Where(m => m.ConversationId == c.Id).ToList();
                var last = inConversation.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).FirstOrDefault();
                var unread = inConversation.Count(m => m.SenderId != userId && m.MessageType != ChatMessageType.System && m.CreatedAt > mine.LastReadAt);
                return new ConversationListRow(c, last, unread);
            }).ToList();
        return Task.FromResult<IReadOnlyList<ConversationListRow>>(rows);
    }

    public Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid conversationId, DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken cancellationToken = default)
    {
        var query = Messages.Where(m => m.ConversationId == conversationId);
        if (before is { } cursor)
            query = query.Where(m => m.CreatedAt < cursor || (beforeId is { } tie && m.CreatedAt == cursor && m.Id.CompareTo(tie) < 0));
        return Task.FromResult<IReadOnlyList<ChatMessage>>([.. query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(limit)]);
    }

    public Task<ChatMessage?> GetMessageAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Messages.FirstOrDefault(m => m.Id == id));

    public Task<ChatMessage?> GetLatestUserMessageAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Messages.Where(m => m.ConversationId == conversationId && m.MessageType != ChatMessageType.System)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).FirstOrDefault());

    public Task<IReadOnlyList<string>> ListCoMemberIdsAsync(string projectCode, string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Conversations
            .Where(c => c.ProjectCode == projectCode && c.FindMember(userId) is not null)
            .SelectMany(c => c.Members).Select(m => m.UserId).Where(id => id != userId).Distinct()]);

    public Task<ChatTotals> GetTotalsAsync(string? projectCode, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var conversations = Conversations.Where(c => projectCode is null || c.ProjectCode == projectCode).ToList();
        var ids = conversations.Select(c => c.Id).ToHashSet();
        var messages = Messages.Count(m => ids.Contains(m.ConversationId) && m.CreatedAt >= from && m.CreatedAt < to && m.MessageType != ChatMessageType.System);
        var direct = conversations.Count(c => c.Type == ConversationType.Direct);
        return Task.FromResult(new ChatTotals(conversations.Count, direct, conversations.Count - direct, messages));
    }

    public Task<IReadOnlyList<MonitoredConversationDto>> ListRecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MonitoredConversationDto>>([.. Conversations
            .Where(c => projectCode is null || c.ProjectCode == projectCode)
            .OrderByDescending(c => c.LastMessageAt).Take(limit)
            .Select(c => new MonitoredConversationDto(c.Id, c.ProjectCode, c.Type.ToString(), c.Title, c.Members.Count,
                Messages.Count(m => m.ConversationId == c.Id && m.MessageType != ChatMessageType.System), c.LastMessageAt))]);
}

/// <summary>Remembers every push. With <see cref="Fail"/> set, each push throws (the message must still be saved and returned).</summary>
internal sealed class RecordingRealtime : IChatRealtime
{
    public bool Fail { get; set; }
    public List<(string Project, string[] Users, ChatMessageDto Message)> Messages { get; } = [];
    public List<(string[] Users, Guid Conversation, string User, DateTimeOffset At)> Reads { get; } = [];
    public List<(string[] Users, Guid Conversation, string User, bool Typing)> Typings { get; } = [];
    public List<(string[] Users, string User, bool Online)> Presences { get; } = [];
    public List<(string[] Users, Guid Conversation)> Changes { get; } = [];

    private Task Gate() => Fail ? throw new InvalidOperationException("the hub is down") : Task.CompletedTask;

    public Task MessageReceivedAsync(string projectCode, IReadOnlyCollection<string> userIds, ChatMessageDto message, CancellationToken cancellationToken = default)
    {
        Messages.Add((projectCode, [.. userIds], message));
        return Gate();
    }

    public Task MessageReadAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, string userId, DateTimeOffset readAt, CancellationToken cancellationToken = default)
    {
        Reads.Add(([.. userIds], conversationId, userId, readAt));
        return Gate();
    }

    public Task TypingAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, string userId, bool isTyping, CancellationToken cancellationToken = default)
    {
        Typings.Add(([.. userIds], conversationId, userId, isTyping));
        return Gate();
    }

    public Task PresenceChangedAsync(string projectCode, IReadOnlyCollection<string> userIds, string userId, bool isOnline, CancellationToken cancellationToken = default)
    {
        Presences.Add(([.. userIds], userId, isOnline));
        return Gate();
    }

    public Task ConversationChangedAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, CancellationToken cancellationToken = default)
    {
        Changes.Add(([.. userIds], conversationId));
        return Gate();
    }
}

internal sealed class ThrowingJobQueue : IJobQueue
{
    public Task<Guid> EnqueueAsync(string queue, string type, object payload, CancellationToken cancellationToken = default, TimeSpan? delay = null) =>
        throw new InvalidOperationException("the queue is down");

    public Task<IReadOnlyList<JobQueueStatusDto>> GetQueuesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RetryFailedResult> RetryFailedAsync(string queue, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Stands in for the notification service: every command is recorded and the answer (or the exception) is set by the test.</summary>
internal sealed class FakeNotificationDispatcher : INotificationDispatcher
{
    public List<DispatchNotificationCommand> Commands { get; } = [];
    public Func<DispatchNotificationCommand, DispatchNotificationResult>? Respond { get; set; }

    public Task<DispatchNotificationResult> DispatchAsync(DispatchNotificationCommand command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        return Task.FromResult(Respond?.Invoke(command) ?? new DispatchNotificationResult(true, "Sent", [], 1, Guid.NewGuid(), []));
    }

    public Task<IReadOnlyList<NotificationTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<NotificationLogDto>> ListLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public static DispatchNotificationResult Failed(string error) =>
        new(false, "Failed", [new NotificationChannelResult("Email", false, error, null, false)], 1, Guid.NewGuid(), []);
}
