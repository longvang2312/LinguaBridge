using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Entities;

namespace LinguaBridge.UnitTests.Learning;

internal sealed class FakeClock : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

    public void AdvanceDays(int days) => UtcNow = UtcNow.AddDays(days);
}

/// <summary>The view of Content that Learning sees: lessons that are published, and nothing else.</summary>
internal sealed class FakeLessonSource : ILessonSource
{
    public Dictionary<Guid, LessonContent> Lessons { get; } = [];

    public Dictionary<Guid, List<Guid>> PublishedLessonIdsByCourse { get; } = [];

    public LessonContent Publish(LessonContent lesson)
    {
        Lessons[lesson.LessonId] = lesson;
        if (!PublishedLessonIdsByCourse.TryGetValue(lesson.CourseId, out var ids))
            PublishedLessonIdsByCourse[lesson.CourseId] = ids = [];
        ids.Add(lesson.LessonId);
        return lesson;
    }

    /// <summary>The lesson stops being visible to learners, as if archived or unpublished.</summary>
    public void Unpublish(Guid lessonId)
    {
        var lesson = Lessons[lessonId];
        Lessons.Remove(lessonId);
        PublishedLessonIdsByCourse[lesson.CourseId].Remove(lessonId);
    }

    public Task<LessonContent?> GetPublishedLessonAsync(Guid lessonId, BaseLanguage baseLanguage, CancellationToken cancellationToken) =>
        Task.FromResult(Lessons.GetValueOrDefault(lessonId));

    public Task<IReadOnlyList<Guid>?> GetPublishedLessonIdsAsync(Guid courseId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>?>(PublishedLessonIdsByCourse.TryGetValue(courseId, out var ids) ? ids.ToList() : null);
}

internal sealed class FakeLearningRepository : ILearningRepository
{
    public List<ExerciseAttempt> Attempts { get; } = [];

    public List<UserCourseProgress> Progress { get; } = [];

    public List<UserVocabularyProgress> Cards { get; } = [];

    public List<ReviewLog> Logs { get; } = [];

    public int SaveCount { get; private set; }

    public bool FailNextSaveWithUniqueViolation { get; set; }

    public Task<ExerciseAttempt?> GetAttemptAsync(Guid attemptId, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.SingleOrDefault(a => a.Id == attemptId && a.UserId == userId));

    public void Add(ExerciseAttempt attempt) => Attempts.Add(attempt);

    public Task<bool> HasCompletedLessonAsync(Guid userId, Guid lessonId, Guid excludingAttemptId, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.Any(a => a.UserId == userId && a.LessonId == lessonId && a.Id != excludingAttemptId && SavedAsCompleted(a)));

    public Task<int> CountCompletedLessonsAsync(Guid userId, IReadOnlyCollection<Guid> lessonIds, CancellationToken cancellationToken) =>
        Task.FromResult(Attempts.Where(a => a.UserId == userId && lessonIds.Contains(a.LessonId) && SavedAsCompleted(a)).Select(a => a.LessonId).Distinct().Count());

    public Task<UserCourseProgress?> GetCourseProgressAsync(Guid userId, Guid courseId, CancellationToken cancellationToken) =>
        Task.FromResult(Progress.SingleOrDefault(p => p.UserId == userId && p.CourseId == courseId));

    public void Add(UserCourseProgress progress) => Progress.Add(progress);

    public Task<UserVocabularyProgress?> GetCardByVocabularyAsync(Guid userId, Guid vocabularyId, CancellationToken cancellationToken) =>
        Task.FromResult(Cards.SingleOrDefault(c => c.UserId == userId && c.VocabularyId == vocabularyId));

    public Task<UserVocabularyProgress?> GetCardAsync(Guid userId, Guid cardId, CancellationToken cancellationToken) =>
        Task.FromResult(Cards.SingleOrDefault(c => c.UserId == userId && c.Id == cardId));

    public void Add(UserVocabularyProgress card) => Cards.Add(card);

    public void Add(ReviewLog log) => Logs.Add(log);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailNextSaveWithUniqueViolation)
        {
            FailNextSaveWithUniqueViolation = false;
            throw new UniqueConstraintViolationException("pk_user_course_progress", new InvalidOperationException("duplicate"));
        }

        SaveCount++;
        foreach (var attempt in Attempts.Where(a => a.IsCompleted))
            Saved.Add(attempt.Id);
        return Task.CompletedTask;
    }

    // An attempt only counts as completed for other requests once the request that completed it has saved,
    // like a database where the change is not visible before the commit.
    private HashSet<Guid> Saved { get; } = [];

    private bool SavedAsCompleted(ExerciseAttempt attempt) => Saved.Contains(attempt.Id);
}

internal sealed class FakeLearningReadModel : ILearningReadModel
{
    public IReadOnlyList<DueCardDto> Cards { get; set; } = [];

    public List<(Guid UserId, DateOnly Today, BaseLanguage Base, TargetLanguage? Target, int Limit)> Calls { get; } = [];

    public Task<IReadOnlyList<DueCardDto>> GetDueCardsAsync(Guid userId, DateOnly today, BaseLanguage baseLanguage, TargetLanguage? targetLanguage, int limit, CancellationToken cancellationToken)
    {
        Calls.Add((userId, today, baseLanguage, targetLanguage, limit));
        return Task.FromResult(Cards);
    }
}

/// <summary>Small builders for lessons and questions with a known answer key.</summary>
internal static class LessonData
{
    public static readonly Guid CorrectOption = Guid.Parse("c0c0c0c0-0000-4000-8000-000000000001");
    public static readonly Guid WrongOption = Guid.Parse("c0c0c0c0-0000-4000-8000-000000000002");

    /// <summary>Choose-one question; <see cref="CorrectOption"/> is right and <see cref="WrongOption"/> is not.</summary>
    public static SourceQuestion Choice(int order = 0, string target = "你好") => new(
        Guid.NewGuid(), order, "MultipleChoice", target, "nǐ hǎo", "https://cdn/audio.mp3", "Chào hỏi nghĩa là gì?", "Đây là lời chào.", "Grammar note",
        [
            new SourceOption(Guid.NewGuid(), 0, "Goodbye", null, false),
            new SourceOption(CorrectOption, 1, "Hello", null, true),
            new SourceOption(WrongOption, 2, "Thanks", null, false),
        ]);

    public static SourceQuestion Typed(int order = 0, string target = "Good morning", string type = "Translate") => new(
        Guid.NewGuid(), order, type, target, "ɡʊd ˈmɔːrnɪŋ", null, "Chào buổi sáng", "Lời chào buổi sáng.", "Typed note", []);

    public static SourceQuestion Pronounce(int order = 0) => new(
        Guid.NewGuid(), order, "Pronounce", "Thank you", null, null, "Cảm ơn", null, null, []);

    public static LessonContent Lesson(IReadOnlyList<SourceQuestion> questions, Guid? courseId = null, int xp = 20, TargetLanguage language = TargetLanguage.English) =>
        new(Guid.NewGuid(), courseId ?? Guid.NewGuid(), language, xp, questions);
}
