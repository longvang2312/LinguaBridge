using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Conversations;

/// <summary>
/// Conversations of the projects. Every call is made by a <see cref="ChatActor"/> and is checked here, once, for both the REST endpoints and
/// the hub: a conversation of another project does not exist; a conversation of this project can only be read or written by its members;
/// only the owner and the admins of a group change who is in it. Pushing to the connected users and the offline notice are best effort:
/// the message is saved first, and a failure of either never loses it or fails the send.
/// </summary>
public sealed class ConversationService(
    IConversationRepository conversations,
    IStoredFileRepository files,
    IUnitOfWork unitOfWork,
    IUserPresenceTracker presence,
    IChatRealtime realtime,
    IJobQueue jobs,
    ConversationSettings settings,
    TimeProvider clock,
    ILogger<ConversationService> logger) : IConversationService
{
    public const string OfflineJobType = "offline-notification";
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 100;
    private const int MaxListed = 100;

    /// <summary>
    /// The time, to the microsecond: PostgreSQL keeps no finer, so a time that is saved and read back is the same one, and a message that was
    /// returned to its sender can be used as the cursor of the history without matching itself again.
    /// </summary>
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % 10));
    }

    // ------------------------------------------------------------------------------------------------ conversations

    public async Task<CreateConversationResult> CreateAsync(ChatActor actor, CreateConversationCommand command, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(command.Type)) throw Invalid("Type", "Use Direct or Group.");
        var now = Now();
        var others = DistinctOthers(actor, command.Members ?? []);

        Conversation conversation;
        try
        {
            if (command.Type == ConversationType.Direct)
            {
                if (others.Count != 1) throw Invalid("Members", "A direct conversation needs exactly one other user.");

                var existing = await conversations.FindDirectAsync(actor.Project, Conversation.DirectKeyOf(actor.UserId, others[0].UserId), cancellationToken);
                if (existing is not null)
                {
                    ApplyProfiles(existing, actor, command.Self, others);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    return new CreateConversationResult(ToDto(existing), false);
                }

                conversation = Conversation.CreateDirect(actor.Project, actor.UserId, others[0].UserId, now);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(command.Title)) throw Invalid("Title", "A group needs a title.");
                if (others.Count + 1 > Conversation.MaxMembers) throw Invalid("Members", $"A group has at most {Conversation.MaxMembers} members.");

                conversation = Conversation.CreateGroup(actor.Project, command.Title, actor.UserId, now);
                foreach (var other in others) conversation.AddMember(other.UserId, ConversationRole.Member, now);
            }
        }
        catch (DomainException ex)
        {
            throw Invalid("Conversation", ex.Message);
        }

        ApplyProfiles(conversation, actor, command.Self, others);
        conversations.Add(conversation);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (command.Type == ConversationType.Direct && ex is not OperationCanceledException)
        {
            // Two requests made the same pair's conversation at once and the unique index let one win: answer with the winner's.
            conversations.Discard(conversation);
            var winner = await conversations.FindDirectAsync(actor.Project, conversation.DirectKey!, cancellationToken);
            if (winner is null) throw;
            logger.LogInformation(ex, "A direct conversation was created twice at once; the existing one is returned.");
            return new CreateConversationResult(ToDto(winner), false);
        }

        if (conversation.Type == ConversationType.Group) await PushConversationChangedAsync(conversation, [], cancellationToken);
        return new CreateConversationResult(ToDto(conversation), true);
    }

    public async Task<IReadOnlyList<ConversationSummaryDto>> ListAsync(ChatActor actor, CancellationToken cancellationToken = default)
    {
        var rows = await conversations.ListForUserAsync(actor.Project, actor.UserId, MaxListed, cancellationToken);
        return [.. rows.Select(row => new ConversationSummaryDto(
            row.Conversation.Id, row.Conversation.Type.ToString(), row.Conversation.Title, row.Conversation.LastMessageAt,
            row.Last is null ? null : ToDto(row.Last), row.Unread, row.Conversation.FindMember(actor.UserId)?.IsMuted ?? false,
            [.. row.Conversation.Members.Select(m => ToDto(m, actor.Project))]))];
    }

    public async Task<ConversationDto> GetAsync(ChatActor actor, Guid conversationId, CancellationToken cancellationToken = default)
    {
        var (conversation, _) = await LoadAsMemberAsync(actor, conversationId, cancellationToken);
        return ToDto(conversation);
    }

    public async Task<ConversationDto> RenameAsync(ChatActor actor, Guid conversationId, string title, CancellationToken cancellationToken = default)
    {
        var (conversation, member) = await LoadAsMemberAsync(actor, conversationId, cancellationToken);
        if (conversation.Type != ConversationType.Group) throw Invalid("Title", "Only a group has a title.");
        RequireManager(member, "rename the group");
        try
        {
            conversation.Rename(title);
        }
        catch (DomainException ex)
        {
            throw Invalid("Title", ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await PushConversationChangedAsync(conversation, [], cancellationToken);
        return ToDto(conversation);
    }

    // ------------------------------------------------------------------------------------------------ messages

    public async Task<MessagePageDto> GetMessagesAsync(
        ChatActor actor, Guid conversationId, DateTimeOffset? before, Guid? beforeId, int? limit, CancellationToken cancellationToken = default)
    {
        var (conversation, _) = await LoadAsMemberAsync(actor, conversationId, cancellationToken);
        var size = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        // One more than asked for tells whether there is an older page, without a second query.
        var rows = await conversations.ListMessagesAsync(conversation.Id, before, beforeId, size + 1, cancellationToken);
        var page = rows.Take(size).ToList();
        var hasMore = rows.Count > size;
        var last = page.Count > 0 ? page[^1] : null;
        return new MessagePageDto([.. page.Select(m => ToDto(m))], hasMore, hasMore ? last?.CreatedAt : null, hasMore ? last?.Id : null);
    }

    public async Task<ChatMessageDto> SendAsync(ChatActor actor, Guid conversationId, string content, Guid? attachmentFileId, CancellationToken cancellationToken = default)
    {
        var (conversation, member) = await LoadAsMemberAsync(actor, conversationId, cancellationToken);
        if (attachmentFileId is { } fileId)
        {
            // A file of another project must not be attached: it would hand its download link to this conversation.
            var file = await files.GetByIdAsync(fileId, cancellationToken);
            if (file is null || file.ProjectCode != conversation.ProjectCode) throw Invalid("AttachmentFileId", "That file does not exist in this project.");
        }

        var now = Now();
        ChatMessage message;
        try
        {
            message = ChatMessage.Create(conversation.Id, actor.UserId, actor.Name, content, attachmentFileId, now);
        }
        catch (DomainException ex)
        {
            throw Invalid("Content", ex.Message);
        }

        conversations.AddMessage(message);
        conversation.TouchLastMessage(now);
        member.MarkRead(now);   // writing is reading
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var dto = ToDto(message);
        await PushMessageAsync(conversation, dto, cancellationToken);
        await ScheduleOfflineNoticesAsync(actor, conversation, message, cancellationToken);
        return dto;
    }

    public async Task<DateTimeOffset> MarkReadAsync(ChatActor actor, Guid conversationId, CancellationToken cancellationToken = default)
    {
        var (conversation, member) = await LoadAsMemberAsync(actor, conversationId, cancellationToken);
        var before = member.LastReadAt;
        member.MarkRead(Now());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (member.LastReadAt > before)
            await TryAsync(() => realtime.MessageReadAsync(conversation.ProjectCode, UserIds(conversation), conversation.Id, actor.UserId, member.LastReadAt, cancellationToken), "message read");
        return member.LastReadAt;
    }

    // ------------------------------------------------------------------------------------------------ members

    public async Task<ConversationDto> AddMembersAsync(ChatActor actor, Guid conversationId, IReadOnlyList<MemberSpec> members, CancellationToken cancellationToken = default)
    {
        var (conversation, actingMember) = await LoadAsMemberAsync(actor, conversationId, cancellationToken);
        if (conversation.Type != ConversationType.Group) throw Invalid("Conversation", "Members can only be added to a group.");
        RequireManager(actingMember, "add members");

        var now = Now();
        var added = new List<ConversationMember>();
        foreach (var spec in DistinctOthers(actor, members ?? []))
        {
            if (conversation.FindMember(spec.UserId) is { } present)
            {
                present.SetProfile(spec.DisplayName, spec.Email, spec.ChatId);   // already in: only refresh its contact
                continue;
            }

            try
            {
                var member = conversation.AddMember(spec.UserId, ConversationRole.Member, now);
                member.SetProfile(spec.DisplayName, spec.Email, spec.ChatId);
                added.Add(member);
                var joined = ChatMessage.System(conversation.Id, $"{NameOf(member)} đã tham gia nhóm", now);
                conversations.AddMessage(joined);
                conversation.TouchLastMessage(now);
            }
            catch (DomainException ex)
            {
                throw Invalid("Members", ex.Message);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (added.Count > 0) await PushConversationChangedAsync(conversation, [], cancellationToken);
        return ToDto(conversation);
    }

    public async Task RemoveMemberAsync(ChatActor actor, Guid conversationId, string userId, CancellationToken cancellationToken = default)
    {
        var (conversation, actingMember) = await LoadAsMemberAsync(actor, conversationId, cancellationToken);
        if (conversation.Type != ConversationType.Group) throw Invalid("Conversation", "Only a group can be left or changed; a direct conversation stays as it is.");

        var target = conversation.FindMember(userId) ?? throw new NotFoundException($"'{userId}' is not in this conversation.");
        var leaving = string.Equals(target.UserId, actor.UserId, StringComparison.Ordinal);
        if (!leaving)
        {
            RequireManager(actingMember, "remove members");
            // An admin looks after the members; the owner and the other admins can only be removed by the owner.
            if (target.Role != ConversationRole.Member && actingMember.Role != ConversationRole.Owner)
                throw new ConversationAccessDeniedException("Only the owner can remove an admin or the owner.");
        }

        var now = Now();
        var name = NameOf(target);
        var wasOwner = target.Role == ConversationRole.Owner;
        conversation.Members.Remove(target);
        conversations.RemoveMember(target);

        if (wasOwner && conversation.Members.Count > 0)
        {
            // A group is never left without an owner: the longest-standing admin, otherwise the longest-standing member, takes over.
            var heir = conversation.Members.OrderBy(m => m.Role == ConversationRole.Admin ? 0 : 1).ThenBy(m => m.JoinedAt).First();
            heir.SetRole(ConversationRole.Owner);
        }

        var text = leaving ? $"{name} đã rời nhóm" : $"{name} đã bị xóa khỏi nhóm";
        var notice = ChatMessage.System(conversation.Id, text, now);
        conversations.AddMessage(notice);
        conversation.TouchLastMessage(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await PushMessageAsync(conversation, ToDto(notice), cancellationToken);
        await PushConversationChangedAsync(conversation, [target.UserId], cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------ realtime support

    public async Task<IReadOnlyList<string>> GetPeersAsync(ChatActor actor, Guid conversationId, CancellationToken cancellationToken = default)
    {
        var (conversation, _) = await LoadAsMemberAsync(actor, conversationId, cancellationToken);
        return [.. UserIds(conversation).Where(id => !string.Equals(id, actor.UserId, StringComparison.Ordinal))];
    }

    public async Task<IReadOnlyList<string>> GetOnlinePeersAsync(ChatActor actor, CancellationToken cancellationToken = default)
    {
        var peers = await conversations.ListCoMemberIdsAsync(actor.Project, actor.UserId, cancellationToken);
        return [.. peers.Where(id => presence.IsOnline(actor.Project, id))];
    }

    public async Task AnnouncePresenceAsync(string projectCode, string userId, bool isOnline, CancellationToken cancellationToken = default)
    {
        var peers = await conversations.ListCoMemberIdsAsync(projectCode, userId, cancellationToken);
        if (peers.Count == 0) return;
        await TryAsync(() => realtime.PresenceChangedAsync(projectCode, peers, userId, isOnline, cancellationToken), "presence");
    }

    public async Task AnnounceTypingAsync(ChatActor actor, Guid conversationId, bool isTyping, CancellationToken cancellationToken = default)
    {
        var peers = await GetPeersAsync(actor, conversationId, cancellationToken);
        if (peers.Count == 0) return;
        await TryAsync(() => realtime.TypingAsync(actor.Project, peers, conversationId, actor.UserId, isTyping, cancellationToken), "typing");
    }

    // ------------------------------------------------------------------------------------------------ steps

    private async Task<(Conversation Conversation, ConversationMember Member)> LoadAsMemberAsync(ChatActor actor, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetAsync(actor.Project, conversationId, cancellationToken)
                           ?? throw new NotFoundException("There is no such conversation.");
        var member = conversation.FindMember(actor.UserId)
                     ?? throw new ConversationAccessDeniedException("You are not a member of this conversation.");
        return (conversation, member);
    }

    private static void RequireManager(ConversationMember member, string what)
    {
        if (member.Role is not (ConversationRole.Owner or ConversationRole.Admin))
            throw new ConversationAccessDeniedException($"Only the owner or an admin of the group can {what}.");
    }

    /// <summary>The members to add: no duplicates, never the caller (who is added by the service), each id checked.</summary>
    private static List<MemberSpec> DistinctOthers(ChatActor actor, IReadOnlyList<MemberSpec> members)
    {
        var result = new List<MemberSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { actor.UserId };
        foreach (var spec in members)
        {
            if (spec is null || !ChatIdentity.IsValidUserId(spec.UserId))
                throw Invalid("Members", "A user id is 1 to 100 letters, digits or . _ @ : -");
            if (seen.Add(spec.UserId)) result.Add(spec);
        }

        return result;
    }

    private static void ApplyProfiles(Conversation conversation, ChatActor actor, MemberSpec? self, IReadOnlyList<MemberSpec> others)
    {
        conversation.FindMember(actor.UserId)?.SetProfile(self?.DisplayName ?? actor.Name, self?.Email, self?.ChatId);
        foreach (var spec in others) conversation.FindMember(spec.UserId)?.SetProfile(spec.DisplayName, spec.Email, spec.ChatId);
    }

    private async Task PushMessageAsync(Conversation conversation, ChatMessageDto dto, CancellationToken cancellationToken) =>
        await TryAsync(() => realtime.MessageReceivedAsync(conversation.ProjectCode, UserIds(conversation), dto, cancellationToken), "message");

    private async Task PushConversationChangedAsync(Conversation conversation, IReadOnlyCollection<string> alsoTell, CancellationToken cancellationToken)
    {
        var audience = UserIds(conversation).Concat(alsoTell).Distinct(StringComparer.Ordinal).ToList();
        await TryAsync(() => realtime.ConversationChangedAsync(conversation.ProjectCode, audience, conversation.Id, cancellationToken), "conversation");
    }

    /// <summary>
    /// A member who is offline, has not muted the conversation and left a way to reach them gets a notice after the grace period (the job
    /// looks again when it runs: by then the user may be back, may have read the message, or a newer message may have taken over).
    /// </summary>
    private async Task ScheduleOfflineNoticesAsync(ChatActor actor, Conversation conversation, ChatMessage message, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(0, settings.OfflineGraceSeconds));
        foreach (var member in conversation.Members)
        {
            if (string.Equals(member.UserId, message.SenderId, StringComparison.Ordinal) || member.IsMuted) continue;
            if ((member.Email is null || !actor.MayEmail) && (member.ChatId is null || !actor.MayBot)) continue;   // nothing the sender is allowed to use
            if (presence.IsOnline(conversation.ProjectCode, member.UserId)) continue;

            await TryAsync(
                () => jobs.EnqueueAsync(JobQueues.Chat, OfflineJobType, new OfflineNotificationPayload(conversation.ProjectCode, conversation.Id, message.Id, member.UserId, actor.MayEmail, actor.MayBot), cancellationToken, delay),
                "offline notice");
        }
    }

    private async Task TryAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The {What} could not be pushed or scheduled; the data is saved.", what);
        }
    }

    private static string[] UserIds(Conversation conversation) => [.. conversation.Members.Select(m => m.UserId)];

    private static string NameOf(ConversationMember member) => member.DisplayName ?? member.UserId;

    private static ValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });

    private ConversationDto ToDto(Conversation c) => new(
        c.Id, c.ProjectCode, c.Type.ToString(), c.Title, c.CreatedBy, c.CreatedAt, c.LastMessageAt, [.. c.Members.OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId, StringComparer.Ordinal).Select(m => ToDto(m, c.ProjectCode))]);

    private ConversationMemberDto ToDto(ConversationMember m, string project) =>
        new(m.UserId, NameOf(m), m.Role.ToString(), m.JoinedAt, presence.IsOnline(project, m.UserId));

    public static ChatMessageDto ToDto(ChatMessage m) =>
        new(m.Id, m.ConversationId, m.SenderId, m.SenderName, m.MessageType.ToString(), m.Content, m.AttachmentFileId, m.CreatedAt);
}
