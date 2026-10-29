using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content.Abstractions;

namespace LinguaBridge.Modules.Content.Queries;

/// <summary>Courses a learner may see: published only.</summary>
public sealed record GetPublishedCoursesQuery(BaseLanguage BaseLanguage, TargetLanguage TargetLanguage);

/// <summary>Every course in every status, for admins. <c>TargetLanguage</c> of null means all languages.</summary>
public sealed record GetAllCoursesQuery(TargetLanguage? TargetLanguage, BaseLanguage BaseLanguage = BaseLanguage.Vietnamese);

/// <param name="IncludeUnpublished">True for admins. A learner asking for an unpublished course gets "not found".</param>
public sealed record GetCourseDetailQuery(Guid CourseId, BaseLanguage BaseLanguage, bool IncludeUnpublished);

/// <param name="IncludeUnpublished">True for admins: also returns draft, review and archived lessons, and works on unpublished courses.</param>
public sealed record GetLessonsQuery(Guid CourseId, BaseLanguage BaseLanguage, bool IncludeUnpublished);

/// <summary>Admin-only: a lesson in any status with its questions and correct answers.</summary>
public sealed record PreviewLessonQuery(Guid LessonId, BaseLanguage BaseLanguage);

public sealed class GetPublishedCoursesHandler(IContentReadModel readModel)
{
    public async Task<Result<IReadOnlyList<CourseSummaryDto>>> HandleAsync(GetPublishedCoursesQuery query, CancellationToken cancellationToken = default) =>
        Result<IReadOnlyList<CourseSummaryDto>>.Success(
            await readModel.GetPublishedCoursesAsync(query.BaseLanguage, query.TargetLanguage, cancellationToken));
}

public sealed class GetAllCoursesHandler(IContentReadModel readModel)
{
    public async Task<Result<IReadOnlyList<CourseSummaryDto>>> HandleAsync(GetAllCoursesQuery query, CancellationToken cancellationToken = default) =>
        Result<IReadOnlyList<CourseSummaryDto>>.Success(
            await readModel.GetAllCoursesAsync(query.TargetLanguage, query.BaseLanguage, cancellationToken));
}

public sealed class GetCourseDetailHandler(IContentReadModel readModel)
{
    public async Task<Result<CourseDetailDto>> HandleAsync(GetCourseDetailQuery query, CancellationToken cancellationToken = default)
    {
        var course = await readModel.GetCourseDetailAsync(query.CourseId, query.BaseLanguage, query.IncludeUnpublished, cancellationToken);
        return course is null ? Error.NotFound("course.not_found", $"Course {query.CourseId} was not found.") : course;
    }
}

public sealed class GetLessonsHandler(IContentReadModel readModel)
{
    public async Task<Result<IReadOnlyList<LessonDto>>> HandleAsync(GetLessonsQuery query, CancellationToken cancellationToken = default)
    {
        var lessons = await readModel.GetLessonsAsync(query.CourseId, query.BaseLanguage, query.IncludeUnpublished, cancellationToken);
        return lessons is null
            ? Error.NotFound("course.not_found", $"Course {query.CourseId} was not found.")
            : Result<IReadOnlyList<LessonDto>>.Success(lessons);
    }
}

public sealed class PreviewLessonHandler(IContentReadModel readModel)
{
    public async Task<Result<LessonPreviewDto>> HandleAsync(PreviewLessonQuery query, CancellationToken cancellationToken = default)
    {
        var preview = await readModel.PreviewLessonAsync(query.LessonId, query.BaseLanguage, cancellationToken);
        return preview is null ? Error.NotFound("lesson.not_found", $"Lesson {query.LessonId} was not found.") : preview;
    }
}
