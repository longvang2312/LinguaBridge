using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.Modules.Content.Abstractions;

/// <summary>
/// Write side of the module. Implemented by Persistence; the module itself knows nothing about EF Core.
/// Loaded aggregates are tracked, so changes made to them are saved by <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IContentRepository
{
    Task<bool> CourseCodeExistsAsync(string code, CancellationToken cancellationToken);

    /// <summary>The course with its translations.</summary>
    Task<Course?> GetCourseAsync(Guid courseId, CancellationToken cancellationToken);

    /// <summary>The lesson with its translations.</summary>
    Task<Lesson?> GetLessonAsync(Guid lessonId, CancellationToken cancellationToken);

    /// <summary>The question with its options and translations.</summary>
    Task<Question?> GetQuestionAsync(Guid questionId, CancellationToken cancellationToken);

    Task<bool> HasPublishedLessonAsync(Guid courseId, CancellationToken cancellationToken);

    /// <summary>One past the highest order index used by the course's lessons (0 when it has none).</summary>
    Task<int> GetNextLessonOrderIndexAsync(Guid courseId, CancellationToken cancellationToken);

    /// <summary>One past the highest order index used by the lesson's questions (0 when it has none).</summary>
    Task<int> GetNextQuestionOrderIndexAsync(Guid lessonId, CancellationToken cancellationToken);

    void Add(Course course);

    void Add(Lesson lesson);

    void Add(Question question);

    /// <exception cref="LinguaBridge.Application.Abstractions.UniqueConstraintViolationException">A unique key was taken meanwhile.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
