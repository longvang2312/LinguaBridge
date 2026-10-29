using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.Content.Entities;

/// <summary>One answer choice of a <see cref="Question"/>.</summary>
public class QuestionOption : BaseEntity
{
    public const int PinyinOrIpaMaxLength = 255;

    // Required by EF Core.
    private QuestionOption() { }

    internal QuestionOption(Guid questionId, string optionText, bool isCorrect, int orderIndex, string? pinyinOrIpa)
    {
        QuestionId = Guard.RequiredId(questionId, nameof(questionId));
        OptionText = Guard.Required(optionText, int.MaxValue, nameof(optionText));
        IsCorrect = isCorrect;
        OrderIndex = Guard.NonNegative(orderIndex, nameof(orderIndex));
        PinyinOrIpa = Guard.Optional(pinyinOrIpa, PinyinOrIpaMaxLength, nameof(pinyinOrIpa));
    }

    public Guid QuestionId { get; private set; }
    public string OptionText { get; private set; } = string.Empty;
    public string? PinyinOrIpa { get; private set; }
    public bool IsCorrect { get; private set; }
    public int OrderIndex { get; private set; }
}
