using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Content.Entities;
using LinguaBridge.Modules.Learning.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace LinguaBridge.Infrastructure.Persistence.Learning;

internal sealed class LearningReadModel(LinguaBridgeDbContext db) : ILearningReadModel
{
    public async Task<IReadOnlyList<DueCardDto>> GetDueCardsAsync(
        Guid userId,
        DateOnly today,
        BaseLanguage baseLanguage,
        TargetLanguage? targetLanguage,
        int limit,
        CancellationToken cancellationToken)
    {
        // Joined in the query so the limit applies after dropping cards whose content is no longer published.
        var due =
            from card in db.UserVocabularyProgress.AsNoTracking()
            join question in db.Questions.AsNoTracking() on card.VocabularyId equals question.Id
            join lesson in db.Lessons.AsNoTracking() on question.LessonId equals lesson.Id
            join course in db.Courses.AsNoTracking() on lesson.CourseId equals course.Id
            where card.UserId == userId
                && card.NextReviewDate <= today
                && lesson.Status == ContentStatus.Published
                && course.Status == ContentStatus.Published
            select new { Card = card, Question = question };

        if (targetLanguage is { } language)
            due = due.Where(x => x.Card.TargetLanguage == language);

        var rows = await due
            .OrderBy(x => x.Card.NextReviewDate)
            .ThenBy(x => x.Card.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return [];

        var questionIds = rows.Select(r => r.Question.Id).ToArray();
        var translations = (await db.QuestionTranslations.AsNoTracking()
                .Where(t => questionIds.Contains(t.QuestionId))
                .ToListAsync(cancellationToken))
            .ToLookup(t => t.QuestionId);

        return rows.Select(r =>
        {
            var prompt = TranslationResolver.Pick(translations[r.Question.Id], baseLanguage, t => t.BaseLanguage);

            return new DueCardDto(
                r.Card.Id,
                r.Card.VocabularyId,
                r.Card.TargetLanguage.ToCode(),
                r.Question.TargetText,
                r.Question.PinyinOrIpa,
                r.Question.AudioPromptUrl,
                prompt?.PromptTranslation,
                r.Card.State.ToString(),
                r.Card.Repetitions,
                r.Card.IntervalDays,
                r.Card.NextReviewDate);
        }).ToList();
    }
}
