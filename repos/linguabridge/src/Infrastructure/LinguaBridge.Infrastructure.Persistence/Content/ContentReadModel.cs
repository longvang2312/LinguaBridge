using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Content.Abstractions;
using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinguaBridge.Infrastructure.Persistence.Content;

/// <summary>Read side of the Content module: no tracking, DTOs only. Translations are resolved in memory (see <see cref="TranslationResolver"/>).</summary>
internal sealed class ContentReadModel(LinguaBridgeDbContext db) : IContentReadModel
{
    public async Task<IReadOnlyList<CourseSummaryDto>> GetPublishedCoursesAsync(BaseLanguage baseLanguage, TargetLanguage targetLanguage, CancellationToken cancellationToken)
    {
        var courses = await db.Courses.AsNoTracking()
            .Include(c => c.Translations)
            .Where(c => c.Status == ContentStatus.Published && c.TargetLanguage == targetLanguage)
            .OrderBy(c => c.Code)
            .ToListAsync(cancellationToken);

        return courses.Select(c => ToSummary(c, baseLanguage)).ToList();
    }

    public async Task<IReadOnlyList<CourseSummaryDto>> GetAllCoursesAsync(TargetLanguage? targetLanguage, BaseLanguage baseLanguage, CancellationToken cancellationToken)
    {
        var query = db.Courses.AsNoTracking().Include(c => c.Translations).AsQueryable();
        if (targetLanguage is { } language)
            query = query.Where(c => c.TargetLanguage == language);

        var courses = await query.OrderBy(c => c.Code).ToListAsync(cancellationToken);

        return courses.Select(c => ToSummary(c, baseLanguage)).ToList();
    }

    public async Task<CourseDetailDto?> GetCourseDetailAsync(Guid courseId, BaseLanguage baseLanguage, bool includeUnpublished, CancellationToken cancellationToken)
    {
        var course = await db.Courses.AsNoTracking()
            .Include(c => c.Translations)
            .SingleOrDefaultAsync(c => c.Id == courseId, cancellationToken);

        if (course is null || !IsVisible(course.Status, includeUnpublished))
            return null;

        var translations = course.Translations
            .OrderBy(t => t.BaseLanguage)
            .Select(t => new CourseTranslationDto(t.BaseLanguage.ToCode(), t.Title, t.Description))
            .ToList();

        return new CourseDetailDto(ToSummary(course, baseLanguage), translations);
    }

    public async Task<IReadOnlyList<LessonDto>?> GetLessonsAsync(Guid courseId, BaseLanguage baseLanguage, bool includeUnpublished, CancellationToken cancellationToken)
    {
        var courseStatus = await db.Courses.AsNoTracking()
            .Where(c => c.Id == courseId)
            .Select(c => (ContentStatus?)c.Status)
            .SingleOrDefaultAsync(cancellationToken);

        if (courseStatus is null || !IsVisible(courseStatus.Value, includeUnpublished))
            return null;

        var query = db.Lessons.AsNoTracking().Include(l => l.Translations).Where(l => l.CourseId == courseId);
        if (!includeUnpublished)
            query = query.Where(l => l.Status == ContentStatus.Published);

        var lessons = await query.OrderBy(l => l.OrderIndex).ThenBy(l => l.CreatedAt).ToListAsync(cancellationToken);

        return lessons.Select(l => ToDto(l, baseLanguage)).ToList();
    }

    public async Task<LessonPreviewDto?> PreviewLessonAsync(Guid lessonId, BaseLanguage baseLanguage, CancellationToken cancellationToken)
    {
        var lesson = await db.Lessons.AsNoTracking()
            .Include(l => l.Translations)
            .SingleOrDefaultAsync(l => l.Id == lessonId, cancellationToken);
        if (lesson is null)
            return null;

        var questions = await db.Questions.AsNoTracking()
            .Include(q => q.Translations)
            .Include(q => q.Options)
            .AsSplitQuery()
            .Where(q => q.LessonId == lessonId)
            .OrderBy(q => q.OrderIndex)
            .ToListAsync(cancellationToken);

        return new LessonPreviewDto(ToDto(lesson, baseLanguage), questions.Select(q => ToDto(q, baseLanguage)).ToList());
    }

    /// <summary>Learners see published content only; everything else is reported as absent so its existence is not revealed.</summary>
    private static bool IsVisible(ContentStatus status, bool includeUnpublished) =>
        includeUnpublished || status == ContentStatus.Published;

    private static CourseSummaryDto ToSummary(Course course, BaseLanguage baseLanguage)
    {
        var translation = TranslationResolver.Pick(course.Translations, baseLanguage, t => t.BaseLanguage);

        return new CourseSummaryDto(
            course.Id,
            course.Code,
            course.TargetLanguage.ToCode(),
            course.Level.ToString().ToUpperInvariant(),
            course.Status.ToString(),
            course.Version,
            course.ThumbnailUrl,
            translation?.Title,
            translation?.Description,
            translation?.BaseLanguage.ToCode(),
            course.UpdatedAt);
    }

    private static LessonDto ToDto(Lesson lesson, BaseLanguage baseLanguage)
    {
        var translation = TranslationResolver.Pick(lesson.Translations, baseLanguage, t => t.BaseLanguage);

        return new LessonDto(
            lesson.Id,
            lesson.CourseId,
            lesson.OrderIndex,
            lesson.LessonType.ToString(),
            lesson.Status.ToString(),
            lesson.XpReward,
            lesson.Version,
            translation?.Title,
            translation?.GuideText,
            translation?.BaseLanguage.ToCode());
    }

    private static QuestionDto ToDto(Question question, BaseLanguage baseLanguage)
    {
        var translation = TranslationResolver.Pick(question.Translations, baseLanguage, t => t.BaseLanguage);
        var options = question.Options
            .OrderBy(o => o.OrderIndex)
            .Select(o => new QuestionOptionDto(o.Id, o.OrderIndex, o.OptionText, o.PinyinOrIpa, o.IsCorrect))
            .ToList();

        return new QuestionDto(
            question.Id,
            question.OrderIndex,
            question.QuestionType.ToString(),
            question.AudioPromptUrl,
            question.TargetText,
            question.PinyinOrIpa,
            question.GrammarNotes,
            translation?.PromptTranslation,
            translation?.Explanation,
            translation?.BaseLanguage.ToCode(),
            options);
    }
}
