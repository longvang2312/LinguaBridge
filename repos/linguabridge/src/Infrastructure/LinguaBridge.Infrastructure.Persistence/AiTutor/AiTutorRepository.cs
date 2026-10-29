using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinguaBridge.Infrastructure.Persistence.AiTutor;

/// <summary>Reads and writes only the <c>ai</c> schema. Sessions are always looked up together with their owner.</summary>
internal sealed class AiTutorRepository(LinguaBridgeDbContext db) : IAiTutorRepository
{
    public Task<ChatSession?> GetSessionAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken) =>
        db.ChatSessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
            return [];

        var newestFirst = await Ordered(db.ChatMessages.AsNoTracking().Where(m => m.SessionId == sessionId), newestFirst: true)
            .Take(count)
            .ToListAsync(cancellationToken);

        newestFirst.Reverse();
        return newestFirst;
    }

    public async Task<(IReadOnlyList<ChatMessage> Items, int Total)> GetMessagesAsync(Guid sessionId, int offset, int limit, CancellationToken cancellationToken)
    {
        var messages = db.ChatMessages.AsNoTracking().Where(m => m.SessionId == sessionId);

        var total = await messages.CountAsync(cancellationToken);
        var items = await Ordered(messages, newestFirst: false).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<AiUsage> GetUsageSinceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var entries = db.AiUsageLedger.AsNoTracking().Where(l => l.UserId == userId && l.CreatedAt >= since);

        var tokens = await entries.SumAsync(l => (long?)l.TotalTokens, cancellationToken) ?? 0;
        var requests = await entries.CountAsync(cancellationToken);
        return new AiUsage(tokens, requests);
    }

    public async Task<bool> TryReserveAsync(AiUsageLedger reservation, DateTimeOffset windowStart, Func<AiUsage, bool> isAllowed, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // One learner's reservations queue up here, so each sees the ones before it. The lock is held to the end of
        // this transaction only, never while the model is being called.
        var key = reservation.UserId.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);

        var usage = await GetUsageSinceAsync(reservation.UserId, windowStart, cancellationToken);
        if (!isAllowed(usage))
            return false;

        db.AiUsageLedger.Add(reservation);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public void Remove(AiUsageLedger reservation) => db.AiUsageLedger.Remove(reservation);

    public void Add(ChatSession session) => db.ChatSessions.Add(session);

    public void Add(ChatMessage message) => db.ChatMessages.Add(message);

    public void Add(AiUsageLedger entry) => db.AiUsageLedger.Add(entry);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// By time, and for two messages stamped in the same instant by sender: a learner's message always comes before
    /// the tutor's reply to it, and "User" sorts after "Assistant".
    /// </summary>
    private static IOrderedQueryable<ChatMessage> Ordered(IQueryable<ChatMessage> messages, bool newestFirst) => newestFirst
        ? messages.OrderByDescending(m => m.CreatedAt).ThenBy(m => m.SenderType)
        : messages.OrderBy(m => m.CreatedAt).ThenByDescending(m => m.SenderType);
}
