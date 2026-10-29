using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Content.Entities;

/// <summary>Question prompt and explanation in one base language. Keyed by (QuestionId, BaseLanguage).</summary>
public class QuestionTranslation
{
    // Required by EF Core.
    private QuestionTranslation() { }

    internal QuestionTranslation(Guid questionId, BaseLanguage baseLanguage, string promptTranslation, string? explanation)
    {
        QuestionId = Guard.RequiredId(questionId, nameof(questionId));
        BaseLanguage = baseLanguage;
        Update(promptTranslation, explanation);
    }

    public Guid QuestionId { get; private set; }
    public BaseLanguage BaseLanguage { get; private set; }
    public string PromptTranslation { get; private set; } = string.Empty;
    public string? Explanation { get; private set; }

    internal void Update(string promptTranslation, string? explanation)
    {
        PromptTranslation = Guard.Required(promptTranslation, int.MaxValue, nameof(promptTranslation));
        Explanation = Guard.Optional(explanation, int.MaxValue, nameof(explanation));
    }
}
