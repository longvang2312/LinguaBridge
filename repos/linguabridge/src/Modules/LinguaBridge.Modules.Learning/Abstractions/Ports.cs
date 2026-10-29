using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Learning.Entities;

namespace LinguaBridge.Modules.Learning.Abstractions;

/// <summary>
/// What Learning needs to know about Content, expressed in Learning's own terms. Learning never references the
/// Content module; Persistence answers these questions by reading the content tables. Everything returned here
/// is limited to content a learner may actually see: a published lesson in a published course.
/// </summary>
public interface ILessonSource
{
    /// <summary>The lesson with its questions and answer key, or null if it does not exist or is not published.</summary>
    Task<LessonContent?> GetPublishedLessonAsync(Guid lessonId, BaseLanguage baseLanguage, CancellationToken cancellationToken);

    /// <summary>The ids of the course's published lessons, or null if the course does not exist or is not published.</summary>
    Task<IReadOnlyList<Guid>?> GetPublishedLessonIdsAsync(Guid courseId, CancellationToken cancellationToken);
}

/// <summary>
/// A lesson as Learning needs it. It carries the answer key (<see cref="SourceOption.IsCorrect"/>, the expected
/// text), so it is for server-side grading only and must be mapped to a learner DTO before leaving the module.
/// </summary>
public sealed record LessonContent(Guid LessonId, Guid CourseId, TargetLanguage TargetLanguage, int XpReward, IReadOnlyList<SourceQuestion> Questions);

public sealed record SourceQuestion(
    Guid Id,
    int OrderIndex,
    string QuestionType,
    string TargetText,
    string? PinyinOrIpa,
    string? AudioPromptUrl,
    string? PromptTranslation,
    string? Explanation,
    string? GrammarNotes,
    IReadOnlyList<SourceOption> Options);

public sealed record SourceOption(Guid Id, int OrderIndex, string Text, string? PinyinOrIpa, bool IsCorrect);

/// <summary>
/// Write side of the module, over its own schemas only (<c>learning</c> and <c>srs</c>). Everything is scoped to a
/// user, so one learner can never load or change another's data. Loaded entities are tracked.
/// </summary>
public interface ILearningRepository
{
    /// <summary>The attempt with its responses; null if it does not exist or belongs to someone else.</summary>
    Task<ExerciseAttempt?> GetAttemptAsync(Guid attemptId, Guid userId, CancellationToken cancellationToken);

    void Add(ExerciseAttempt attempt);

    /// <summary>Whether the user finished some attempt at the lesson other than <paramref name="excludingAttemptId"/>.</summary>
    Task<bool> HasCompletedLessonAsync(Guid userId, Guid lessonId, Guid excludingAttemptId, CancellationToken cancellationToken);

    /// <summary>How many different lessons among <paramref name="lessonIds"/> the user has finished at least once.</summary>
    Task<int> CountCompletedLessonsAsync(Guid userId, IReadOnlyCollection<Guid> lessonIds, CancellationToken cancellationToken);

    Task<UserCourseProgress?> GetCourseProgressAsync(Guid userId, Guid courseId, CancellationToken cancellationToken);

    void Add(UserCourseProgress progress);

    /// <summary>The user's card for a vocabulary item (a question id); null if they have none yet.</summary>
    Task<UserVocabularyProgress?> GetCardByVocabularyAsync(Guid userId, Guid vocabularyId, CancellationToken cancellationToken);

    /// <summary>The card by its own id; null if it does not exist or belongs to someone else.</summary>
    Task<UserVocabularyProgress?> GetCardAsync(Guid userId, Guid cardId, CancellationToken cancellationToken);

    void Add(UserVocabularyProgress card);

    void Add(ReviewLog log);

    /// <exception cref="LinguaBridge.Application.Abstractions.UniqueConstraintViolationException">A concurrent request created the same row first.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Read side that has to join the learner's cards with content, so the page size is right after filtering.</summary>
public interface ILearningReadModel
{
    /// <summary>
    /// The user's cards due on or before <paramref name="today"/>, oldest first, limited to cards whose question is
    /// still published (a card for archived content is not offered).
    /// </summary>
    Task<IReadOnlyList<DueCardDto>> GetDueCardsAsync(
        Guid userId,
        DateOnly today,
        BaseLanguage baseLanguage,
        TargetLanguage? targetLanguage,
        int limit,
        CancellationToken cancellationToken);
}
