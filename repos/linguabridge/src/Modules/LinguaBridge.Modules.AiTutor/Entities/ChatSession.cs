using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.AiTutor.Entities;

/// <summary>
/// A tutoring conversation around one scenario (e.g. <c>JobInterview</c>, <c>BusinessEmail</c>, <c>FreeTalk</c>).
/// Messages are not held in memory here because a session can grow without bound; <see cref="AddMessage"/>
/// creates them for the caller to persist.
/// </summary>
public class ChatSession : AggregateRoot
{
    public const int ScenarioCodeMaxLength = 50;
    public const int TitleMaxLength = 255;

    // Required by EF Core.
    private ChatSession() { }

    public ChatSession(Guid userId, TargetLanguage targetLanguage, string scenarioCode, string title)
    {
        UserId = Guard.RequiredId(userId, nameof(userId));
        TargetLanguage = targetLanguage;
        ScenarioCode = Guard.Required(scenarioCode, ScenarioCodeMaxLength, nameof(scenarioCode));
        Title = Guard.Required(title, TitleMaxLength, nameof(title));
        StartedAt = DateTimeOffset.UtcNow;
        TotalMessagesCount = 0;
    }

    public Guid UserId { get; private set; }
    public TargetLanguage TargetLanguage { get; private set; }
    public string ScenarioCode { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public int TotalMessagesCount { get; private set; }

    public bool IsEnded => EndedAt.HasValue;

    public ChatMessage AddMessage(
        SenderType senderType,
        string content,
        string? pinyinOrIpa = null,
        string? translation = null,
        string? grammarCorrection = null)
    {
        if (IsEnded)
            throw new InvalidOperationException("Cannot add a message to an ended chat session.");

        var message = new ChatMessage(Id, senderType, content, pinyinOrIpa, translation, grammarCorrection);
        TotalMessagesCount++;
        return message;
    }

    public void End()
    {
        EndedAt ??= DateTimeOffset.UtcNow;
    }
}
