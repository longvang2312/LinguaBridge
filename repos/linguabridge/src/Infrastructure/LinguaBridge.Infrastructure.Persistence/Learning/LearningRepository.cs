using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LinguaBridge.Infrastructure.Persistence.Learning;

/// <summary>Reads and writes only the <c>learning</c> and <c>srs</c> schemas. Every lookup is filtered by user.</summary>
internal sealed class LearningRepository(LinguaBridgeDbContext db) : ILearningRepository
{
    public Task<ExerciseAttempt?> GetAttemptAsync(Guid attemptId, Guid userId, CancellationToken cancellationToken) =>
        db.ExerciseAttempts
            .Include(a => a.Responses)
            .SingleOrDefaultAsync(a => a.Id == attemptId && a.UserId == userId, cancellationToken);

    public void Add(ExerciseAttempt attempt) => db.ExerciseAttempts.Add(attempt);

    public Task<bool> HasCompletedLessonAsync(Guid userId, Guid lessonId, Guid excludingAttemptId, CancellationToken cancellationToken) =>
        db.ExerciseAttempts.AnyAsync(
            a => a.UserId == userId && a.LessonId == lessonId && a.Id != excludingAttemptId && a.CompletedAt != null,
            cancellationToken);

    public Task<int> CountCompletedLessonsAsync(Guid userId, IReadOnlyCollection<Guid> lessonIds, CancellationToken cancellationToken)
    {
        var ids = lessonIds.ToArray();
        return db.ExerciseAttempts
            .Where(a => a.UserId == userId && a.CompletedAt != null && ids.Contains(a.LessonId))
            .Select(a => a.LessonId)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    public Task<UserCourseProgress?> GetCourseProgressAsync(Guid userId, Guid courseId, CancellationToken cancellationToken) =>
        db.UserCourseProgress.SingleOrDefaultAsync(p => p.UserId == userId && p.CourseId == courseId, cancellationToken);

    public void Add(UserCourseProgress progress) => db.UserCourseProgress.Add(progress);

    public Task<UserVocabularyProgress?> GetCardByVocabularyAsync(Guid userId, Guid vocabularyId, CancellationToken cancellationToken) =>
        db.UserVocabularyProgress.SingleOrDefaultAsync(c => c.UserId == userId && c.VocabularyId == vocabularyId, cancellationToken);

    public Task<UserVocabularyProgress?> GetCardAsync(Guid userId, Guid cardId, CancellationToken cancellationToken) =>
        db.UserVocabularyProgress.SingleOrDefaultAsync(c => c.UserId == userId && c.Id == cardId, cancellationToken);

    public void Add(UserVocabularyProgress card) => db.UserVocabularyProgress.Add(card);

    public void Add(ReviewLog log) => db.ReviewLogs.Add(log);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            throw new UniqueConstraintViolationException(postgres.ConstraintName, ex);
        }
    }
}
