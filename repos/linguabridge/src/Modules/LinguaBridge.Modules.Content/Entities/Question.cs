using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Content.Entities;

/// <summary>An exercise in a lesson: the target-language text plus its options and base-language prompts.</summary>
public class Question : AggregateRoot
{
    public const int AudioPromptUrlMaxLength = 500;
    public const int PinyinOrIpaMaxLength = 255;

    private readonly List<QuestionTranslation> _translations = [];
    private readonly List<QuestionOption> _options = [];

    // Required by EF Core.
    private Question() { }

    public Question(
        Guid lessonId,
        int orderIndex,
        QuestionType questionType,
        string targetText,
        string? pinyinOrIpa = null,
        string? audioPromptUrl = null,
        string? grammarNotes = null)
    {
        LessonId = Guard.RequiredId(lessonId, nameof(lessonId));
        OrderIndex = Guard.NonNegative(orderIndex, nameof(orderIndex));
        QuestionType = questionType;
        TargetText = Guard.Required(targetText, int.MaxValue, nameof(targetText));
        PinyinOrIpa = Guard.Optional(pinyinOrIpa, PinyinOrIpaMaxLength, nameof(pinyinOrIpa));
        AudioPromptUrl = Guard.Optional(audioPromptUrl, AudioPromptUrlMaxLength, nameof(audioPromptUrl));
        GrammarNotes = Guard.Optional(grammarNotes, int.MaxValue, nameof(grammarNotes));
    }

    public Guid LessonId { get; private set; }
    public int OrderIndex { get; private set; }
    public QuestionType QuestionType { get; private set; }
    public string? AudioPromptUrl { get; private set; }

    /// <summary>The English or Chinese text being learned.</summary>
    public string TargetText { get; private set; } = string.Empty;

    /// <summary>Pinyin for Chinese, IPA for English.</summary>
    public string? PinyinOrIpa { get; private set; }

    /// <summary>Shown when the learner answers wrongly.</summary>
    public string? GrammarNotes { get; private set; }

    public IReadOnlyCollection<QuestionTranslation> Translations => _translations.AsReadOnly();
    public IReadOnlyCollection<QuestionOption> Options => _options.AsReadOnly();

    /// <summary>Adds the translation for <paramref name="baseLanguage"/>, or replaces the existing one.</summary>
    public void SetTranslation(BaseLanguage baseLanguage, string promptTranslation, string? explanation = null)
    {
        var existing = _translations.SingleOrDefault(t => t.BaseLanguage == baseLanguage);
        if (existing is null)
            _translations.Add(new QuestionTranslation(Id, baseLanguage, promptTranslation, explanation));
        else
            existing.Update(promptTranslation, explanation);
    }

    /// <summary>Appends an option; options are ordered in the sequence they are added.</summary>
    public QuestionOption AddOption(string optionText, bool isCorrect, string? pinyinOrIpa = null)
    {
        var option = new QuestionOption(Id, optionText, isCorrect, _options.Count, pinyinOrIpa);
        _options.Add(option);
        return option;
    }
}
