using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Content.Entities;
using LinguaBridge.Modules.Learning.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace LinguaBridge.Infrastructure.Persistence.Learning;

/// <summary>
/// Answers Learning's questions about Content by reading the content schema. Modules never reference each other;
/// this is the one place where both are visible. Only published lessons of published courses are ever returned.
/// </summary>
internal sealed class LessonSource(LinguaBridgeDbContext db) : ILessonSource
{
    public async Task<LessonContent?> GetPublishedLessonAsync(Guid lessonId, BaseLanguage baseLanguage, CancellationToken cancellationToken)
    {
        var lesson = await (
                from l in db.Lessons.AsNoTracking()
                join c in db.Courses.AsNoTracking() on l.CourseId equals c.Id
                where l.Id == lessonId && l.Status == ContentStatus.Published && c.Status == ContentStatus.Published
                select new { l.Id, l.CourseId, l.XpReward, c.TargetLanguage })
            .SingleOrDefaultAsync(cancellationToken);
        if (lesson is null)
            return null;

        var questions = await db.Questions.AsNoTracking()
            .Include(q => q.Translations)
            .Include(q => q.Options)
            .AsSplitQuery()
            .Where(q => q.LessonId == lessonId)
            .OrderBy(q => q.OrderIndex)
            .ToListAsync(cancellationToken);

        return new LessonContent(
            lesson.Id,
            lesson.CourseId,
            lesson.TargetLanguage,
            lesson.XpReward,
            questions.Select(q => ToSource(q, baseLanguage)).ToList());
    }

    public async Task<IReadOnlyList<Guid>?> GetPublishedLessonIdsAsync(Guid courseId, CancellationToken cancellationToken)
    {
        var published = await db.Courses.AsNoTracking().AnyAsync(c => c.Id == courseId && c.Status == ContentStatus.Published, cancellationToken);
        if (!published)
            return null;

        return await db.Lessons.AsNoTracking()
            .Where(l => l.CourseId == courseId && l.Status == ContentStatus.Published)
            .OrderBy(l => l.OrderIndex)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);
    }

    private static SourceQuestion ToSource(Question question, BaseLanguage baseLanguage)
    {
        var translation = TranslationResolver.Pick(question.Translations, baseLanguage, t => t.BaseLanguage);

        return new SourceQuestion(
            question.Id,
            question.OrderIndex,
            question.QuestionType.ToString(),
            question.TargetText,
            question.PinyinOrIpa,
            question.AudioPromptUrl,
            translation?.PromptTranslation,
            translation?.Explanation,
            question.GrammarNotes,
            question.Options
                .OrderBy(o => o.OrderIndex)
                .Select(o => new SourceOption(o.Id, o.OrderIndex, o.OptionText, o.PinyinOrIpa, o.IsCorrect))
                .ToList());
    }
}
