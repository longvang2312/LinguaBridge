using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Content.Abstractions;

/// <summary>
/// Read side of the module: returns ready-to-serve DTOs, never entities. Visibility is decided by the caller
/// through <c>includeUnpublished</c>; a thing the caller may not see is reported as absent (null), not as forbidden.
/// </summary>
public interface IContentReadModel
{
    Task<IReadOnlyList<CourseSummaryDto>> GetPublishedCoursesAsync(BaseLanguage baseLanguage, TargetLanguage targetLanguage, CancellationToken cancellationToken);

    Task<IReadOnlyList<CourseSummaryDto>> GetAllCoursesAsync(TargetLanguage? targetLanguage, BaseLanguage baseLanguage, CancellationToken cancellationToken);

    /// <summary>Null when the course does not exist, or is not published and <paramref name="includeUnpublished"/> is false.</summary>
    Task<CourseDetailDto?> GetCourseDetailAsync(Guid courseId, BaseLanguage baseLanguage, bool includeUnpublished, CancellationToken cancellationToken);

    /// <summary>
    /// The course's lessons in order. Null when the course does not exist, or is not published and
    /// <paramref name="includeUnpublished"/> is false. Without the flag only published lessons are returned.
    /// </summary>
    Task<IReadOnlyList<LessonDto>?> GetLessonsAsync(Guid courseId, BaseLanguage baseLanguage, bool includeUnpublished, CancellationToken cancellationToken);

    /// <summary>The lesson in any status, with its questions, options and the correct answers. Null when it does not exist.</summary>
    Task<LessonPreviewDto?> PreviewLessonAsync(Guid lessonId, BaseLanguage baseLanguage, CancellationToken cancellationToken);
}
