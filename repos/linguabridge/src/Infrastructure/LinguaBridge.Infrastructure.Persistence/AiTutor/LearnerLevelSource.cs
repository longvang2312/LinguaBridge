using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinguaBridge.Infrastructure.Persistence.AiTutor;

/// <summary>
/// Reads the learner's level from their Learning progress: the highest level among the courses of this language in
/// which they have finished at least one lesson. A newcomer starts at A1 (English) or HSK 1 (Chinese).
/// </summary>
internal sealed class LearnerLevelSource(LinguaBridgeDbContext db) : ILearnerLevelSource
{
    public async Task<LanguageLevel> GetLevelAsync(Guid userId, TargetLanguage targetLanguage, CancellationToken cancellationToken)
    {
        var levels = await (
                from progress in db.UserCourseProgress.AsNoTracking()
                join course in db.Courses.AsNoTracking() on progress.CourseId equals course.Id
                where progress.UserId == userId && progress.CompletedLessonsCount > 0 && course.TargetLanguage == targetLanguage
                select course.Level)
            .Distinct()
            .ToListAsync(cancellationToken);

        // The enum is ordered by difficulty within each language, but it is stored by name, so compare here, not in SQL.
        return levels.Count > 0
            ? levels.Max()
            : targetLanguage == TargetLanguage.English ? LanguageLevel.A1 : LanguageLevel.Hsk1;
    }
}
