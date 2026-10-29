namespace LinguaBridge.Modules.AiTutor.Abstractions;

public sealed record ChatSessionDto(
    Guid Id,
    string TargetLanguage,
    string ScenarioCode,
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int TotalMessagesCount);

public sealed record ChatMessageDto(
    Guid Id,
    string Sender,
    string Content,
    string? PinyinOrIpa,
    string? Translation,
    string? GrammarTips,
    DateTimeOffset CreatedAt);

public sealed record MessagesPageDto(IReadOnlyList<ChatMessageDto> Items, int Total, int Offset, int Limit);

/// <param name="IsStructured">False when the model ignored the JSON format and its whole text was used as the reply.</param>
public sealed record TutorReplyDto(
    Guid MessageId,
    string ReplyText,
    string? PinyinOrIpa,
    string? Translation,
    string? GrammarTips,
    int TokensUsed,
    bool IsStructured,
    DateTimeOffset CreatedAt);

/// <summary>
/// Today's AI allowance. A free learner is limited by number of requests, a paid learner by tokens; the limit that
/// does not apply is null.
/// </summary>
public sealed record AiQuotaDto(
    string Plan,
    int? DailyRequestLimit,
    int RequestsToday,
    int? RequestsRemaining,
    long? DailyTokenLimit,
    long TokensToday,
    long? TokensRemaining,
    DateTimeOffset ResetsAt);
