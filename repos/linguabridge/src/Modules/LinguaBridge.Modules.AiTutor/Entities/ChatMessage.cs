using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.AiTutor.Entities;

/// <summary>One turn of a <see cref="ChatSession"/>, with the tutor's optional transcription, translation and correction.</summary>
public class ChatMessage : BaseEntity
{
    // Required by EF Core.
    private ChatMessage() { }

    internal ChatMessage(
        Guid sessionId,
        SenderType senderType,
        string content,
        string? pinyinOrIpa,
        string? translation,
        string? grammarCorrection)
    {
        SessionId = Guard.RequiredId(sessionId, nameof(sessionId));
        SenderType = senderType;
        Content = Guard.Required(content, int.MaxValue, nameof(content));
        PinyinOrIpa = Guard.Optional(pinyinOrIpa, int.MaxValue, nameof(pinyinOrIpa));
        Translation = Guard.Optional(translation, int.MaxValue, nameof(translation));
        GrammarCorrection = Guard.Optional(grammarCorrection, int.MaxValue, nameof(grammarCorrection));
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid SessionId { get; private set; }
    public SenderType SenderType { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public string? PinyinOrIpa { get; private set; }

    /// <summary>Translation into the learner's base language.</summary>
    public string? Translation { get; private set; }

    public string? GrammarCorrection { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
