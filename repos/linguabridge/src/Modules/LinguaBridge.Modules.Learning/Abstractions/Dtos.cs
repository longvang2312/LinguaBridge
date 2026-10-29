namespace LinguaBridge.Modules.Learning.Abstractions;

/// <summary>An answer choice as a learner sees it. There is deliberately no correctness flag on this type.</summary>
public sealed record LearnerOptionDto(Guid Id, int OrderIndex, string Text, string? PinyinOrIpa);

/// <summary>
/// A question as a learner sees it. Nothing here reveals the answer: options carry no correctness flag, and for
/// questions answered by typing, <c>TargetText</c> and <c>PinyinOrIpa</c> (which are the answer) are null.
/// </summary>
public sealed record LearnerQuestionDto(
    Guid Id,
    int OrderIndex,
    string QuestionType,
    string? AudioPromptUrl,
    string? TargetText,
    string? PinyinOrIpa,
    string? PromptTranslation,
    IReadOnlyList<LearnerOptionDto> Options);

public sealed record LearnerQuizDto(Guid LessonId, Guid CourseId, int XpReward, IReadOnlyList<LearnerQuestionDto> Questions);

public sealed record AttemptStartedDto(Guid AttemptId, Guid LessonId, int TotalQuestions, DateTimeOffset StartedAt);

/// <summary>Instant feedback. The answer is revealed only now, after the learner has committed to theirs.</summary>
public sealed record ResponseResultDto(
    Guid QuestionId,
    bool IsCorrect,
    Guid? CorrectOptionId,
    string? CorrectAnswer,
    string? Explanation,
    string? GrammarNotes,
    int AnsweredCount,
    int TotalQuestions);

public sealed record CourseProgressDto(
    Guid CourseId,
    int CompletedLessonsCount,
    int TotalLessonsCount,
    bool IsCompleted,
    DateTimeOffset? LastAccessedAt);

public sealed record AttemptCompletedDto(
    Guid AttemptId,
    int ScorePercentage,
    int CorrectCount,
    int TotalQuestions,
    int XpEarned,
    bool IsFirstCompletion,
    CourseProgressDto Progress);

public sealed record DueCardDto(
    Guid CardId,
    Guid VocabularyId,
    string TargetLanguage,
    string TargetText,
    string? PinyinOrIpa,
    string? AudioPromptUrl,
    string? PromptTranslation,
    string State,
    int Repetitions,
    int IntervalDays,
    DateOnly NextReviewDate);

public sealed record ReviewResultDto(
    Guid CardId,
    int Rating,
    double EaseFactor,
    int IntervalDays,
    int Repetitions,
    DateOnly NextReviewDate,
    string State);
