using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Domain;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.Persistence;

public sealed class ConversationRepository(SharedServicesDbContext db) : IConversationRepository
{
    public void Add(Conversation conversation) => db.Conversations.Add(conversation);

    public void AddMessage(ChatMessage message) => db.ChatMessages.Add(message);

    public void RemoveMember(ConversationMember member) => db.ConversationMembers.Remove(member);

    public void Discard(Conversation conversation)
    {
        // A copy: detaching a member makes EF take it out of the collection being walked.
        foreach (var member in conversation.Members.ToList()) db.Entry(member).State = EntityState.Detached;
        db.Entry(conversation).State = EntityState.Detached;
    }

    public Task<Conversation?> GetAsync(string projectCode, Guid id, CancellationToken cancellationToken = default) =>
        db.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.ProjectCode == projectCode && c.Id == id, cancellationToken);

    public Task<Conversation?> FindDirectAsync(string projectCode, string directKey, CancellationToken cancellationToken = default) =>
        db.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.ProjectCode == projectCode && c.DirectKey == directKey, cancellationToken);

    public async Task<IReadOnlyList<ConversationListRow>> ListForUserAsync(string projectCode, string userId, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await db.Conversations.AsNoTracking().Include(c => c.Members)
            .Where(c => c.ProjectCode == projectCode && c.Members.Any(m => m.UserId == userId))
            .OrderByDescending(c => c.LastMessageAt).ThenBy(c => c.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];

        var ids = rows.Select(c => c.Id).ToList();

        // The newest message of each conversation (several only if two share the very same instant: one of them is used).
        var latest = (await db.ChatMessages.AsNoTracking()
                .Where(m => ids.Contains(m.ConversationId)
                            && m.CreatedAt == db.ChatMessages.Where(x => x.ConversationId == m.ConversationId).Max(x => x.CreatedAt))
                .ToListAsync(cancellationToken))
            .GroupBy(m => m.ConversationId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Id).First());

        // Unread: written by someone else after the time the user last read.
        var unread = await (from message in db.ChatMessages.AsNoTracking()
                            join member in db.ConversationMembers on message.ConversationId equals member.ConversationId
                            where member.UserId == userId && ids.Contains(message.ConversationId)
                                  && message.SenderId != userId && message.MessageType != ChatMessageType.System
                                  && message.CreatedAt > member.LastReadAt
                            group message by message.ConversationId into g
                            select new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return [.. rows.Select(c => new ConversationListRow(c, latest.GetValueOrDefault(c.Id), unread.GetValueOrDefault(c.Id)))];
    }

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid conversationId, DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken cancellationToken = default)
    {
        var query = db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (before is { } cursor)
        {
            query = beforeId is { } tie
                ? query.Where(m => m.CreatedAt < cursor || (m.CreatedAt == cursor && m.Id.CompareTo(tie) < 0))
                : query.Where(m => m.CreatedAt < cursor);
        }

        return await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<ChatMessage?> GetMessageAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<ChatMessage?> GetLatestUserMessageAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.MessageType != ChatMessageType.System)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> ListCoMemberIdsAsync(string projectCode, string userId, CancellationToken cancellationToken = default) =>
        await (from c in db.Conversations.AsNoTracking()
               where c.ProjectCode == projectCode && c.Members.Any(x => x.UserId == userId)
               from m in c.Members
               where m.UserId != userId
               select m.UserId).Distinct().ToListAsync(cancellationToken);

    public async Task<ChatTotals> GetTotalsAsync(string? projectCode, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var conversations = db.Conversations.AsNoTracking().Where(c => projectCode == null || c.ProjectCode == projectCode);
        var total = await conversations.CountAsync(cancellationToken);
        var direct = await conversations.CountAsync(c => c.Type == ConversationType.Direct, cancellationToken);

        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.CreatedAt >= from && m.CreatedAt < to && m.MessageType != ChatMessageType.System
                        && (projectCode == null || db.Conversations.Any(c => c.Id == m.ConversationId && c.ProjectCode == projectCode)))
            .CountAsync(cancellationToken);
        return new ChatTotals(total, direct, total - direct, messages);
    }

    public async Task<IReadOnlyList<MonitoredConversationDto>> ListRecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await db.Conversations.AsNoTracking()
            .Where(c => projectCode == null || c.ProjectCode == projectCode)
            .OrderByDescending(c => c.LastMessageAt).ThenBy(c => c.Id)
            .Take(limit)
            .Select(c => new
            {
                c.Id, c.ProjectCode, c.Type, c.Title, c.LastMessageAt,
                Members = c.Members.Count,
                Messages = db.ChatMessages.Count(m => m.ConversationId == c.Id && m.MessageType != ChatMessageType.System),
            })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new MonitoredConversationDto(r.Id, r.ProjectCode, r.Type.ToString(), r.Title, r.Members, r.Messages, r.LastMessageAt))];
    }
}
