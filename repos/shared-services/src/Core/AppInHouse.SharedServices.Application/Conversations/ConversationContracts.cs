using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Conversations;

/// <summary>
/// The user who is acting in the chat: the project the conversation belongs to, the user's id in that project's own user base, and the
/// name shown beside the messages. The API builds it from the credential of the caller; nothing here trusts a value the user typed in a message.
/// <para><see cref="MayEmail"/> and <see cref="MayBot"/> say whether the credential may send email (<c>email:send</c>) and bot messages
/// (<c>chat:send</c>). The offline notice of a message goes out by those ways only, so a credential that can only chat cannot make the
/// service send email or bot messages to an address it chose: the checks of the notification endpoints are not bypassed.</para>
/// </summary>
public sealed record ChatActor(string Project, string UserId, string Name, bool MayEmail = false, bool MayBot = false)
{
    /// <summary>One string for the pair, the identity SignalR and the presence tracker use (a user id is only unique inside its project).</summary>
    public string Key => ChatKeys.Of(Project, UserId);
}

public static class ChatKeys
{
    public static string Of(string project, string userId) => $"{project}:{userId}";
}

/// <summary>Someone to put in a conversation. The contact fields are where the notification goes when the user is offline.</summary>
public sealed record MemberSpec(string UserId, string? DisplayName = null, string? Email = null, string? ChatId = null);

/// <summary>
/// A direct conversation needs exactly one other member; a group needs a title. <see cref="Self"/> carries the caller's own display name and
/// contact. The caller is always a member: the owner of a group.
/// </summary>
public sealed record CreateConversationCommand(ConversationType Type, string? Title, IReadOnlyList<MemberSpec> Members, MemberSpec? Self = null);

public sealed record ChatMessageDto(
    Guid Id, Guid ConversationId, string SenderId, string SenderName, string Type, string Content, Guid? AttachmentFileId, DateTimeOffset CreatedAt);

public sealed record ConversationMemberDto(string UserId, string DisplayName, string Role, DateTimeOffset JoinedAt, bool IsOnline);

public sealed record ConversationDto(
    Guid Id, string ProjectCode, string Type, string? Title, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt,
    IReadOnlyList<ConversationMemberDto> Members);

public sealed record CreateConversationResult(ConversationDto Conversation, bool Created);

/// <summary>One line of "my conversations": the last message and how many messages the user has not read.</summary>
public sealed record ConversationSummaryDto(
    Guid Id, string Type, string? Title, DateTimeOffset LastMessageAt, ChatMessageDto? LastMessage, int UnreadCount, bool IsMuted,
    IReadOnlyList<ConversationMemberDto> Members);

/// <summary>A page of history, newest first. Pass <see cref="NextBefore"/> (and <see cref="NextBeforeId"/>) back as the cursor to get the older page.</summary>
public sealed record MessagePageDto(IReadOnlyList<ChatMessageDto> Items, bool HasMore, DateTimeOffset? NextBefore, Guid? NextBeforeId);

/// <summary>What a platform operator sees about a conversation: that it exists and how busy it is, never what was said.</summary>
public sealed record MonitoredConversationDto(
    Guid Id, string ProjectCode, string Type, string? Title, int MemberCount, int MessageCount, DateTimeOffset LastMessageAt);

public sealed record ChatMonitorDto(
    string? ProjectCode, int Conversations, int DirectConversations, int GroupConversations, int MessagesToday, int OnlineUsers, int Connections,
    IReadOnlyList<string> OnlineUserIds, IReadOnlyList<MonitoredConversationDto> Recent);

public sealed record ChatTotals(int Conversations, int Direct, int Groups, int MessagesInRange);

/// <summary>The caller may not do this in this conversation (it is not a member, or it is a member without the role the action needs). The API answers 403.</summary>
public sealed class ConversationAccessDeniedException(string message) : Exception(message);

/// <summary>A conversation with the user's view of it, as the list needs it.</summary>
public sealed record ConversationListRow(Conversation Conversation, ChatMessage? Last, int Unread);

public interface IConversationRepository
{
    void Add(Conversation conversation);

    void AddMessage(ChatMessage message);

    void RemoveMember(ConversationMember member);

    /// <summary>Forgets a conversation that was added but could not be saved (two requests created the same direct conversation at once).</summary>
    void Discard(Conversation conversation);

    /// <summary>Tracked, with its members. Only a conversation of this project is found: another project's id is "not found".</summary>
    Task<Conversation?> GetAsync(string projectCode, Guid id, CancellationToken cancellationToken = default);

    Task<Conversation?> FindDirectAsync(string projectCode, string directKey, CancellationToken cancellationToken = default);

    /// <summary>The conversations the user is a member of, the most recently active first. For reading only.</summary>
    Task<IReadOnlyList<ConversationListRow>> ListForUserAsync(string projectCode, string userId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Newest first. With <paramref name="before"/> only messages older than the cursor (the time, then the id as the tie-break).</summary>
    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid conversationId, DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken cancellationToken = default);

    Task<ChatMessage?> GetMessageAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The newest message written by a person (not the service).</summary>
    Task<ChatMessage?> GetLatestUserMessageAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>The ids of everyone who shares a conversation with the user: who should hear that the user went online or offline.</summary>
    Task<IReadOnlyList<string>> ListCoMemberIdsAsync(string projectCode, string userId, CancellationToken cancellationToken = default);

    /// <summary>A null project means every project.</summary>
    Task<ChatTotals> GetTotalsAsync(string? projectCode, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitoredConversationDto>> ListRecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default);
}

/// <summary>What happened to the connections of a user when one was opened or closed.</summary>
public sealed record PresenceChange(string Project, string UserId, bool Changed, bool IsOnline);

/// <summary>Who is connected right now. A user is online while at least one connection (a tab, a phone) is open.</summary>
public interface IUserPresenceTracker
{
    /// <summary>Records a connection. <see cref="PresenceChange.Changed"/> is true when it is the user's first one (the user came online).</summary>
    PresenceChange Connected(string projectCode, string userId, string connectionId);

    /// <summary>Forgets a connection. <see cref="PresenceChange.Changed"/> is true when it was the user's last one (the user went offline).</summary>
    PresenceChange Disconnected(string connectionId);

    bool IsOnline(string projectCode, string userId);

    /// <summary>The ids of the users of the project who are online. A null project means every project (the ids are then prefixed with the project).</summary>
    IReadOnlyList<string> OnlineUsers(string? projectCode);

    int ConnectionCount(string? projectCode);
}

/// <summary>Pushes events to the connected users. The SignalR hub implements it; without a host that has one nothing is pushed.</summary>
public interface IChatRealtime
{
    Task MessageReceivedAsync(string projectCode, IReadOnlyCollection<string> userIds, ChatMessageDto message, CancellationToken cancellationToken = default);

    Task MessageReadAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, string userId, DateTimeOffset readAt, CancellationToken cancellationToken = default);

    Task TypingAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, string userId, bool isTyping, CancellationToken cancellationToken = default);

    Task PresenceChangedAsync(string projectCode, IReadOnlyCollection<string> userIds, string userId, bool isOnline, CancellationToken cancellationToken = default);

    /// <summary>Someone joined, left or was removed, or the group was renamed: clients reload the conversation. The removed user is told too.</summary>
    Task ConversationChangedAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, CancellationToken cancellationToken = default);
}

public interface IConversationService
{
    /// <summary>A direct conversation that already exists is returned (<see cref="CreateConversationResult.Created"/> false), never duplicated.</summary>
    Task<CreateConversationResult> CreateAsync(ChatActor actor, CreateConversationCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSummaryDto>> ListAsync(ChatActor actor, CancellationToken cancellationToken = default);

    Task<ConversationDto> GetAsync(ChatActor actor, Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationDto> RenameAsync(ChatActor actor, Guid conversationId, string title, CancellationToken cancellationToken = default);

    Task<MessagePageDto> GetMessagesAsync(ChatActor actor, Guid conversationId, DateTimeOffset? before, Guid? beforeId, int? limit, CancellationToken cancellationToken = default);

    Task<ChatMessageDto> SendAsync(ChatActor actor, Guid conversationId, string content, Guid? attachmentFileId, CancellationToken cancellationToken = default);

    /// <returns>The time up to which the user has now read.</returns>
    Task<DateTimeOffset> MarkReadAsync(ChatActor actor, Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationDto> AddMembersAsync(ChatActor actor, Guid conversationId, IReadOnlyList<MemberSpec> members, CancellationToken cancellationToken = default);

    /// <summary>Leaves the group (the user is the actor) or removes another member (needs an owner or admin).</summary>
    Task RemoveMemberAsync(ChatActor actor, Guid conversationId, string userId, CancellationToken cancellationToken = default);

    /// <summary>The other members of a conversation the actor belongs to: who should see that the actor is typing. 403 for anyone else.</summary>
    Task<IReadOnlyList<string>> GetPeersAsync(ChatActor actor, Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>The users who share a conversation with the actor and are online now.</summary>
    Task<IReadOnlyList<string>> GetOnlinePeersAsync(ChatActor actor, CancellationToken cancellationToken = default);

    /// <summary>Tells the users who share a conversation with <paramref name="userId"/> that the user came online or went offline.</summary>
    Task AnnouncePresenceAsync(string projectCode, string userId, bool isOnline, CancellationToken cancellationToken = default);

    Task AnnounceTypingAsync(ChatActor actor, Guid conversationId, bool isTyping, CancellationToken cancellationToken = default);
}

public interface IChatMonitor
{
    /// <summary>A null project means every project (a platform caller).</summary>
    Task<ChatMonitorDto> GetAsync(string? projectCode, CancellationToken cancellationToken = default);
}

/// <summary>What the sender of the message was allowed to send (both default to no, so a job queued without them sends nothing outside the chat).</summary>
public sealed record OfflineNotificationPayload(
    string Project, Guid ConversationId, Guid MessageId, string RecipientId, bool AllowEmail = false, bool AllowBot = false);

/// <summary>Runs when the wait after a message ended: tells a member who is still not there. Called by the chat queue's job handler.</summary>
public interface IOfflineNotificationHandler
{
    Task HandleAsync(OfflineNotificationPayload payload, CancellationToken cancellationToken = default);
}

/// <summary>Settings of the conversation service (section <c>Chat</c>). </summary>
public sealed class ConversationSettings
{
    public const string SectionName = "Chat";

    /// <summary>How long a member must stay offline before the notification goes out: someone who just switched tabs is not bothered.</summary>
    public int OfflineGraceSeconds { get; set; } = 20;
}

/// <summary>Used where there is no realtime host (the tests of the service layer): messages are saved and nothing is pushed.</summary>
public sealed class NoChatRealtime : IChatRealtime
{
    public Task MessageReceivedAsync(string projectCode, IReadOnlyCollection<string> userIds, ChatMessageDto message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task MessageReadAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, string userId, DateTimeOffset readAt, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task TypingAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, string userId, bool isTyping, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PresenceChangedAsync(string projectCode, IReadOnlyCollection<string> userIds, string userId, bool isOnline, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ConversationChangedAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
