using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.Learning.Entities;

/// <summary>One run of a learner through a lesson, with the answer given to each question.</summary>
public class ExerciseAttempt : AggregateRoot
{
    private readonly List<QuestionResponse> _responses = [];

    // Required by EF Core.
    private ExerciseAttempt() { }

    public ExerciseAttempt(Guid userId, Guid lessonId)
    {
        UserId = Guard.RequiredId(userId, nameof(userId));
        LessonId = Guard.RequiredId(lessonId, nameof(lessonId));
        ScorePercentage = 0;
        XpEarned = 0;
        DurationSeconds = 0;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }

    /// <summary>Id of a lesson in the Content module; no database FK across module schemas.</summary>
    public Guid LessonId { get; private set; }

    /// <summary>0 to 100.</summary>
    public int ScorePercentage { get; private set; }

    public int XpEarned { get; private set; }
    public int DurationSeconds { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsCompleted => CompletedAt.HasValue;

    public IReadOnlyCollection<QuestionResponse> Responses => _responses.AsReadOnly();

    public QuestionResponse AddResponse(Guid questionId, bool isCorrect, int timeSpentMs, Guid? selectedOptionId = null, string? userInputText = null)
    {
        EnsureNotCompleted();

        var response = new QuestionResponse(Id, questionId, isCorrect, timeSpentMs, selectedOptionId, userInputText);
        _responses.Add(response);
        return response;
    }

    public void Complete(int scorePercentage, int xpEarned)
    {
        EnsureNotCompleted();

        ScorePercentage = Guard.Range(scorePercentage, 0, 100, nameof(scorePercentage));
        XpEarned = Guard.NonNegative(xpEarned, nameof(xpEarned));
        CompletedAt = DateTimeOffset.UtcNow;
        DurationSeconds = (int)Math.Max(0, (CompletedAt.Value - StartedAt).TotalSeconds);
    }

    private void EnsureNotCompleted()
    {
        if (IsCompleted)
            throw new InvalidOperationException("The attempt is already completed.");
    }
}
