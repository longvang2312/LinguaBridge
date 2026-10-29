using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.Learning.Entities;

/// <summary>The learner's answer to one question within an <see cref="ExerciseAttempt"/>.</summary>
public class QuestionResponse : BaseEntity
{
    // Required by EF Core.
    private QuestionResponse() { }

    internal QuestionResponse(Guid attemptId, Guid questionId, bool isCorrect, int timeSpentMs, Guid? selectedOptionId, string? userInputText)
    {
        AttemptId = Guard.RequiredId(attemptId, nameof(attemptId));
        QuestionId = Guard.RequiredId(questionId, nameof(questionId));
        IsCorrect = isCorrect;
        TimeSpentMs = Guard.NonNegative(timeSpentMs, nameof(timeSpentMs));
        SelectedOptionId = selectedOptionId;
        UserInputText = Guard.Optional(userInputText, int.MaxValue, nameof(userInputText));
    }

    public Guid AttemptId { get; private set; }

    /// <summary>Id of a question in the Content module; no database FK across module schemas.</summary>
    public Guid QuestionId { get; private set; }

    /// <summary>Set for multiple-choice questions.</summary>
    public Guid? SelectedOptionId { get; private set; }

    /// <summary>Set for typed or speech-recognised answers.</summary>
    public string? UserInputText { get; private set; }

    public bool IsCorrect { get; private set; }
    public int TimeSpentMs { get; private set; }
}
