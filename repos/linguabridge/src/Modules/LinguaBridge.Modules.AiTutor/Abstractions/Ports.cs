using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.AiTutor.Entities;

namespace LinguaBridge.Modules.AiTutor.Abstractions;

/// <param name="Tokens">Tokens spent since the window start, across every AI task of the learner.</param>
/// <param name="Requests">AI calls made since the window start.</param>
public readonly record struct AiUsage(long Tokens, int Requests);

/// <summary>Storage for chat sessions, their messages and the usage ledger. Every lookup is scoped to the owning learner.</summary>
public interface IAiTutorRepository
{
    Task<ChatSession?> GetSessionAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken);

    /// <summary>The last <paramref name="count"/> messages of a session, oldest first.</summary>
    Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int count, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ChatMessage> Items, int Total)> GetMessagesAsync(Guid sessionId, int offset, int limit, CancellationToken cancellationToken);

    Task<AiUsage> GetUsageSinceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically checks the learner's usage and, only if <paramref name="isAllowed"/> accepts it, stores
    /// <paramref name="reservation"/> as part of that usage. Concurrent calls for the same learner are serialised, so
    /// however many requests arrive together, only as many as the allowance covers are admitted.
    /// </summary>
    /// <returns>False when the allowance does not cover the request; nothing is stored then.</returns>
    Task<bool> TryReserveAsync(AiUsageLedger reservation, DateTimeOffset windowStart, Func<AiUsage, bool> isAllowed, CancellationToken cancellationToken);

    /// <summary>Marks a reservation to be deleted, giving the allowance back; takes effect on the next save.</summary>
    void Remove(AiUsageLedger reservation);

    void Add(ChatSession session);

    void Add(ChatMessage message);

    void Add(AiUsageLedger entry);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What the learner's plan allows. Implemented over the Billing tables until Billing offers its own entitlement
/// service (TASK-016), at which point only the implementation changes.
/// </summary>
public interface IUserEntitlementSource
{
    /// <summary>
    /// The daily AI token quota of the learner's active paid plan, or null when they have none (no entitlement
    /// record, free tier, or an expired plan) and the free allowance applies.
    /// </summary>
    Task<int?> GetPaidDailyTokenQuotaAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>How far along the learner is, taken from their Learning progress, so the tutor speaks at the right level.</summary>
public interface ILearnerLevelSource
{
    /// <summary>The highest level among the courses the learner has started; the first level (A1 / HSK 1) for a newcomer.</summary>
    Task<LanguageLevel> GetLevelAsync(Guid userId, TargetLanguage targetLanguage, CancellationToken cancellationToken);
}
