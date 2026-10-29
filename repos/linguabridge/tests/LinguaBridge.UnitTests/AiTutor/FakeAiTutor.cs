using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.AiTutor;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Commands;
using LinguaBridge.Modules.AiTutor.Entities;
using LinguaBridge.Modules.AiTutor.Quota;
using LinguaBridge.Modules.AiTutor.Queries;
using LinguaBridge.UnitTests.Learning;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinguaBridge.UnitTests.AiTutor;

/// <summary>In-memory storage. Ledger entries are stamped with the fake clock so a test can move to the next day.</summary>
internal sealed class FakeAiTutorRepository(FakeClock clock) : IAiTutorRepository
{
    private readonly List<(AiUsageLedger Entry, DateTimeOffset At)> _ledger = [];

    public List<ChatSession> Sessions { get; } = [];
    public List<ChatMessage> Messages { get; } = [];
    public IReadOnlyList<AiUsageLedger> Ledger => _ledger.Select(l => l.Entry).ToList();
    public int Saves { get; private set; }

    /// <summary>The cancellation token each save was given.</summary>
    public List<CancellationToken> SaveTokens { get; } = [];

    public Task<ChatSession?> GetSessionAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.SingleOrDefault(s => s.Id == sessionId && s.UserId == userId));

    public Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(Messages.Where(m => m.SessionId == sessionId).TakeLast(count).ToList());

    public Task<(IReadOnlyList<ChatMessage> Items, int Total)> GetMessagesAsync(Guid sessionId, int offset, int limit, CancellationToken cancellationToken)
    {
        var all = Messages.Where(m => m.SessionId == sessionId).ToList();
        return Task.FromResult<(IReadOnlyList<ChatMessage>, int)>((all.Skip(offset).Take(limit).ToList(), all.Count));
    }

    public Task<AiUsage> GetUsageSinceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var entries = _ledger.Where(l => l.Entry.UserId == userId && l.At >= since).Select(l => l.Entry).ToList();
        return Task.FromResult(new AiUsage(entries.Sum(e => (long)e.TotalTokens), entries.Count));
    }

    public void Add(ChatSession session) => Sessions.Add(session);

    public void Add(ChatMessage message) => Messages.Add(message);

    public void Add(AiUsageLedger entry) => _ledger.Add((entry, clock.UtcNow));

    public Task<bool> TryReserveAsync(AiUsageLedger reservation, DateTimeOffset windowStart, Func<AiUsage, bool> isAllowed, CancellationToken cancellationToken)
    {
        var usage = GetUsageSinceAsync(reservation.UserId, windowStart, cancellationToken).Result;
        if (!isAllowed(usage))
            return Task.FromResult(false);

        Add(reservation);
        Saves++;
        return Task.FromResult(true);
    }

    public void Remove(AiUsageLedger reservation) => _ledger.RemoveAll(l => l.Entry == reservation);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveTokens.Add(cancellationToken);
        Saves++;
        return Task.CompletedTask;
    }

    /// <summary>Puts spent tokens on the learner's day without going through a request.</summary>
    public void Spend(Guid userId, int tokens) => Add(new AiUsageLedger(userId, "TextTutor", "Test", tokens, 0, 0m));
}

internal sealed class FakeEntitlements : IUserEntitlementSource
{
    private readonly Dictionary<Guid, int> _paid = [];

    public void GrantPaid(Guid userId, int dailyTokenQuota) => _paid[userId] = dailyTokenQuota;

    public Task<int?> GetPaidDailyTokenQuotaAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_paid.TryGetValue(userId, out var quota) ? quota : (int?)null);
}

internal sealed class FakeLevels : ILearnerLevelSource
{
    public LanguageLevel? Level { get; set; }

    public Task<LanguageLevel> GetLevelAsync(Guid userId, TargetLanguage targetLanguage, CancellationToken cancellationToken) =>
        Task.FromResult(Level ?? (targetLanguage == TargetLanguage.English ? LanguageLevel.A1 : LanguageLevel.Hsk1));
}

/// <summary>A model that answers from a script and remembers what it was asked. Never calls anything real.</summary>
internal sealed class ScriptedLlm : ILlmProvider
{
    public const string JsonReply = """{"reply_text":"Tell me about yourself.","pinyin_or_ipa":"/tel mi/","translation":"Hay ke ve ban than.","grammar_tips":"Good start."}""";

    public List<LlmRequest> Requests { get; } = [];
    public Func<LlmRequest, LlmResponse> Respond { get; set; } = _ => new LlmResponse(JsonReply, 120, 30, 0.00004m, "OpenAI_gpt-4o-mini");
    public Exception? Fail { get; set; }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Fail is null ? Task.FromResult(Respond(request)) : Task.FromException<LlmResponse>(Fail);
    }
}

/// <summary>One learner, one clock, in-memory storage and a scripted model, wired the way the host wires them.</summary>
internal sealed class AiHarness
{
    public readonly FakeClock Clock = new();
    public readonly FakeAiTutorRepository Repo;
    public readonly FakeEntitlements Entitlements = new();
    public readonly FakeLevels Levels = new();
    public readonly ScriptedLlm Llm = new();
    public readonly AiTutorOptions Options;
    public readonly Guid User = Guid.NewGuid();

    public AiHarness(AiTutorOptions? options = null)
    {
        Repo = new FakeAiTutorRepository(Clock);
        Options = options ?? new AiTutorOptions();
    }

    public AiQuotaPolicy Quota => new(Repo, Entitlements, Clock, Options);

    public CreateSessionHandler Create => new(Repo, new CreateSessionValidator());

    public SendMessageHandler Send => new(Repo, Levels, Llm, Quota, Options, NullLogger<SendMessageHandler>.Instance, new SendMessageValidator(Options));

    public EndSessionHandler End => new(Repo);

    public GetMessagesHandler Messages => new(Repo);

    public async Task<Guid> NewSessionAsync(
        TargetLanguage language = TargetLanguage.English,
        string scenario = "JobInterview",
        Guid? user = null)
    {
        var result = await Create.HandleAsync(new CreateSessionCommand(user ?? User, language, scenario));
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value.Id;
    }

    public Task<Result<TutorReplyDto>> SayAsync(Guid sessionId, string text, Guid? user = null) =>
        Send.HandleAsync(new SendMessageCommand(user ?? User, sessionId, text));
}
