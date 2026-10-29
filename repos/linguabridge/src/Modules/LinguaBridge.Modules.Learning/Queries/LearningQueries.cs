using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Learning.Abstractions;
using LinguaBridge.Modules.Learning.Commands;
using LinguaBridge.Modules.Learning.Grading;

namespace LinguaBridge.Modules.Learning.Queries;

public sealed record GetQuizQuery(Guid LessonId, BaseLanguage BaseLanguage);

/// <param name="Limit">Clamped to 1-100.</param>
public sealed record GetDueCardsQuery(Guid UserId, BaseLanguage BaseLanguage, TargetLanguage? TargetLanguage, int Limit = GetDueCardsQuery.DefaultLimit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
}

public sealed record GetCourseProgressQuery(Guid UserId, Guid CourseId);

/// <summary>The quiz a learner takes: gradable questions in order, mapped to the DTO that has no answer key.</summary>
public sealed class GetQuizHandler(ILessonSource lessons)
{
    public async Task<Result<LearnerQuizDto>> HandleAsync(GetQuizQuery query, CancellationToken cancellationToken = default)
    {
        var lesson = await lessons.GetPublishedLessonAsync(query.LessonId, query.BaseLanguage, cancellationToken);
        if (lesson is null)
            return CommandSupport.LessonNotFound(query.LessonId);

        return new LearnerQuizDto(
            lesson.LessonId,
            lesson.CourseId,
            lesson.XpReward,
            QuestionGrading.Gradable(lesson).Select(QuestionGrading.ToLearnerDto).ToList());
    }
}

public sealed class GetDueCardsHandler(ILearningReadModel readModel, IDateTimeProvider clock)
{
    public async Task<Result<IReadOnlyList<DueCardDto>>> HandleAsync(GetDueCardsQuery query, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, GetDueCardsQuery.MaxLimit);
        var cards = await readModel.GetDueCardsAsync(query.UserId, clock.TodayUtc(), query.BaseLanguage, query.TargetLanguage, limit, cancellationToken);
        return Result<IReadOnlyList<DueCardDto>>.Success(cards);
    }
}

public sealed class GetCourseProgressHandler(ILearningRepository repository, ILessonSource lessons)
{
    public async Task<Result<CourseProgressDto>> HandleAsync(GetCourseProgressQuery query, CancellationToken cancellationToken = default)
    {
        var publishedLessonIds = await lessons.GetPublishedLessonIdsAsync(query.CourseId, cancellationToken);
        if (publishedLessonIds is null)
            return Error.NotFound("course.not_found", $"Course {query.CourseId} was not found.");

        var progress = await repository.GetCourseProgressAsync(query.UserId, query.CourseId, cancellationToken);
        if (progress is null)
            return new CourseProgressDto(query.CourseId, 0, publishedLessonIds.Count, false, null);

        // The total is today's number of published lessons, which may have grown since the learner last finished one.
        var completed = Math.Min(progress.CompletedLessonsCount, publishedLessonIds.Count);
        return new CourseProgressDto(
            query.CourseId,
            completed,
            publishedLessonIds.Count,
            publishedLessonIds.Count > 0 && completed >= publishedLessonIds.Count,
            progress.LastAccessedAt);
    }
}
