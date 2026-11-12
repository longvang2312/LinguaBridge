namespace AppInHouse.SharedServices.ClientSdk;

/// <summary>
/// The user a conversation call is made for. An API key belongs to the server of a project, which speaks for its users: it names the user here
/// (the id is the project's own user key). <see cref="ProjectCode"/> is only for the platform keys, which may act in any project.
/// </summary>
public sealed record ChatUser(string UserId, string? DisplayName = null, string? ProjectCode = null);

public sealed record ChatMemberRequest(string UserId, string? DisplayName = null, string? Email = null, string? ChatId = null);

/// <summary><c>Type</c> is "Direct" (exactly one other member) or "Group" (needs a title).</summary>
public sealed record CreateConversationRequest(string Type, string? Title = null, IReadOnlyList<ChatMemberRequest>? Members = null, ChatMemberRequest? Self = null);

public sealed record ChatMessageDto(Guid Id, Guid ConversationId, string SenderId, string SenderName, string Type, string Content, Guid? AttachmentFileId, DateTimeOffset CreatedAt);

public sealed record ConversationMemberDto(string UserId, string DisplayName, string Role, DateTimeOffset JoinedAt, bool IsOnline);

public sealed record ConversationDto(
    Guid Id, string ProjectCode, string Type, string? Title, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt,
    IReadOnlyList<ConversationMemberDto> Members);

/// <summary><see cref="Created"/> is false when the direct conversation of the two users already existed.</summary>
public sealed record CreateConversationResponse(ConversationDto Conversation, bool Created);

public sealed record ConversationSummaryDto(
    Guid Id, string Type, string? Title, DateTimeOffset LastMessageAt, ChatMessageDto? LastMessage, int UnreadCount, bool IsMuted,
    IReadOnlyList<ConversationMemberDto> Members);

/// <summary>A page of history, newest first. Pass <see cref="NextBefore"/> and <see cref="NextBeforeId"/> back to get the older page.</summary>
public sealed record MessagePageDto(IReadOnlyList<ChatMessageDto> Items, bool HasMore, DateTimeOffset? NextBefore, Guid? NextBeforeId);

public sealed record MonitoredConversationDto(Guid Id, string ProjectCode, string Type, string? Title, int MemberCount, int MessageCount, DateTimeOffset LastMessageAt);

public sealed record ChatMonitorDto(
    string? ProjectCode, int Conversations, int DirectConversations, int GroupConversations, int MessagesToday, int OnlineUsers, int Connections,
    IReadOnlyList<string> OnlineUserIds, IReadOnlyList<MonitoredConversationDto> Recent);

/// <summary>What asking the realtime hub to negotiate a connection found (nothing is connected, so nobody shows as online).</summary>
public sealed record RealtimeCheckResult(bool Reachable, string Message, IReadOnlyList<string> Transports, int LatencyMs, int? StatusCode);
