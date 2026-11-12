using System.Text.RegularExpressions;

namespace AppInHouse.SharedServices.Domain;

public enum ConversationType
{
    /// <summary>Two people. There is at most one per pair of users in a project.</summary>
    Direct,

    /// <summary>A room with a title and any number of members.</summary>
    Group,
}

public enum ConversationRole
{
    Owner,
    Admin,
    Member,
}

public enum ChatMessageType
{
    Text,

    /// <summary>A message with a file (an image or a document) stored by the storage service.</summary>
    Media,

    /// <summary>Written by the service: someone joined or left.</summary>
    System,
}

/// <summary>What a user id may look like. The id comes from the calling application (its own user key), so it is kept to a safe alphabet.</summary>
public static partial class ChatIdentity
{
    public const int MaxUserIdLength = 100;
    public const int MaxNameLength = 100;

    public static bool IsValidUserId(string? userId) => userId is { Length: > 0 and <= MaxUserIdLength } && UserIdPattern().IsMatch(userId);

    [GeneratedRegex(@"^[A-Za-z0-9._@:\-]+$")]
    private static partial Regex UserIdPattern();
}

/// <summary>A chat room of one project: a conversation between two users or a group. Its messages and members are in their own tables.</summary>
public sealed class Conversation
{
    public const int MaxTitleLength = 200;
    public const int MaxMembers = 200;

    private Conversation()
    {
        ProjectCode = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public string ProjectCode { get; private set; }
    public ConversationType Type { get; private set; }
    public string? Title { get; private set; }

    /// <summary>For a direct conversation, the two user ids in a fixed order; unique per project so the same pair never gets two conversations. Null for a group.</summary>
    public string? DirectKey { get; private set; }

    public string CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastMessageAt { get; private set; }

    public List<ConversationMember> Members { get; private set; } = [];

    public static string DirectKeyOf(string userA, string userB) =>
        string.CompareOrdinal(userA, userB) <= 0 ? $"{userA}|{userB}" : $"{userB}|{userA}";

    public static Conversation CreateDirect(string projectCode, string creator, string other, DateTimeOffset now)
    {
        RequireUser(creator);
        RequireUser(other);
        if (string.Equals(creator, other, StringComparison.Ordinal)) throw new DomainException("A direct conversation needs two different users.");

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), ProjectCode = projectCode, Type = ConversationType.Direct, DirectKey = DirectKeyOf(creator, other),
            CreatedBy = creator, CreatedAt = now, LastMessageAt = now,
        };
        conversation.Members.Add(ConversationMember.Create(conversation.Id, creator, ConversationRole.Member, now));
        conversation.Members.Add(ConversationMember.Create(conversation.Id, other, ConversationRole.Member, now));
        return conversation;
    }

    public static Conversation CreateGroup(string projectCode, string title, string creator, DateTimeOffset now)
    {
        RequireUser(creator);
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), ProjectCode = projectCode, Type = ConversationType.Group, Title = NormalizeTitle(title),
            CreatedBy = creator, CreatedAt = now, LastMessageAt = now,
        };
        conversation.Members.Add(ConversationMember.Create(conversation.Id, creator, ConversationRole.Owner, now));
        return conversation;
    }

    public ConversationMember? FindMember(string userId) => Members.FirstOrDefault(m => string.Equals(m.UserId, userId, StringComparison.Ordinal));

    public ConversationMember AddMember(string userId, ConversationRole role, DateTimeOffset now)
    {
        if (Type != ConversationType.Group) throw new DomainException("Members can only be added to a group.");
        RequireUser(userId);
        if (FindMember(userId) is not null) throw new DomainException($"'{userId}' is already in this conversation.");
        if (Members.Count >= MaxMembers) throw new DomainException($"A group has at most {MaxMembers} members.");

        var member = ConversationMember.Create(Id, userId, role, now);
        Members.Add(member);
        return member;
    }

    public void Rename(string title)
    {
        if (Type != ConversationType.Group) throw new DomainException("Only a group has a title.");
        Title = NormalizeTitle(title);
    }

    public void TouchLastMessage(DateTimeOffset at)
    {
        if (at > LastMessageAt) LastMessageAt = at;
    }

    private static string NormalizeTitle(string? title)
    {
        var text = title?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaxTitleLength) throw new DomainException($"The title of a group is 1 to {MaxTitleLength} characters.");
        return text;
    }

    private static void RequireUser(string userId)
    {
        if (!ChatIdentity.IsValidUserId(userId)) throw new DomainException("A user id is 1 to 100 letters, digits or . _ @ : -");
    }
}

/// <summary>
/// One user in a conversation. <see cref="LastReadAt"/> is how unread messages are counted. The contact fields are where the offline
/// notification goes when the user is not connected; they are never shown to the other members.
/// </summary>
public sealed class ConversationMember
{
    private ConversationMember()
    {
        UserId = string.Empty;
    }

    public Guid ConversationId { get; private set; }
    public string UserId { get; private set; }
    public ConversationRole Role { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }
    public DateTimeOffset LastReadAt { get; private set; }
    public bool IsMuted { get; private set; }

    /// <summary>The name shown in the member list and in notifications.</summary>
    public string? DisplayName { get; private set; }

    public string? Email { get; private set; }
    public string? ChatId { get; private set; }

    public static ConversationMember Create(Guid conversationId, string userId, ConversationRole role, DateTimeOffset now) => new()
    {
        ConversationId = conversationId, UserId = userId, Role = role, JoinedAt = now, LastReadAt = now,
    };

    /// <summary>Time only moves forward: an older read never makes messages unread again.</summary>
    public void MarkRead(DateTimeOffset at)
    {
        if (at > LastReadAt) LastReadAt = at;
    }

    public void SetRole(ConversationRole role) => Role = role;

    public void SetMuted(bool muted) => IsMuted = muted;

    public void SetProfile(string? displayName, string? email, string? chatId)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) DisplayName = Clip(displayName.Trim(), ChatIdentity.MaxNameLength);
        if (!string.IsNullOrWhiteSpace(email)) Email = Clip(email.Trim(), 200);
        if (!string.IsNullOrWhiteSpace(chatId)) ChatId = Clip(chatId.Trim(), 200);
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;
}

public sealed class ChatMessage
{
    public const int MaxContentLength = 4000;

    private ChatMessage()
    {
        SenderId = string.Empty;
        SenderName = string.Empty;
        Content = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public string SenderId { get; private set; }
    public string SenderName { get; private set; }
    public ChatMessageType MessageType { get; private set; }
    public string Content { get; private set; }
    public Guid? AttachmentFileId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>A text message, or a media message when a file is attached (the text may then be empty).</summary>
    public static ChatMessage Create(Guid conversationId, string senderId, string senderName, string content, Guid? attachmentFileId, DateTimeOffset now)
    {
        var text = content?.Trim() ?? string.Empty;
        if (text.Length > MaxContentLength) throw new DomainException($"A message is at most {MaxContentLength} characters.");
        if (text.Length == 0 && attachmentFileId is null) throw new DomainException("A message needs some text or a file.");

        return new ChatMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, SenderId = senderId, SenderName = Name(senderName, senderId),
            MessageType = attachmentFileId is null ? ChatMessageType.Text : ChatMessageType.Media,
            Content = text, AttachmentFileId = attachmentFileId, CreatedAt = now,
        };
    }

    public static ChatMessage System(Guid conversationId, string text, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), ConversationId = conversationId, SenderId = "system", SenderName = "System",
        MessageType = ChatMessageType.System, Content = text.Length > MaxContentLength ? text[..MaxContentLength] : text, CreatedAt = now,
    };

    private static string Name(string name, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        return text.Length > ChatIdentity.MaxNameLength ? text[..ChatIdentity.MaxNameLength] : text;
    }
}
