using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content.Abstractions;
using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.UnitTests.Content;

/// <summary>In-memory stand-in for the Persistence implementation, so handlers are tested without a database.</summary>
internal sealed class FakeContentRepository : IContentRepository
{
    public Dictionary<Guid, Course> Courses { get; } = [];

    public Dictionary<Guid, Lesson> Lessons { get; } = [];

    public Dictionary<Guid, Question> Questions { get; } = [];

    public int SaveCount { get; private set; }

    /// <summary>When set, the next save fails like a lost race on a unique key.</summary>
    public bool FailNextSaveWithUniqueViolation { get; set; }

    public Task<bool> CourseCodeExistsAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(Courses.Values.Any(c => c.Code == code));

    public Task<Course?> GetCourseAsync(Guid courseId, CancellationToken cancellationToken) =>
        Task.FromResult(Courses.GetValueOrDefault(courseId));

    public Task<Lesson?> GetLessonAsync(Guid lessonId, CancellationToken cancellationToken) =>
        Task.FromResult(Lessons.GetValueOrDefault(lessonId));

    public Task<Question?> GetQuestionAsync(Guid questionId, CancellationToken cancellationToken) =>
        Task.FromResult(Questions.GetValueOrDefault(questionId));

    public Task<bool> HasPublishedLessonAsync(Guid courseId, CancellationToken cancellationToken) =>
        Task.FromResult(Lessons.Values.Any(l => l.CourseId == courseId && l.Status == ContentStatus.Published));

    public Task<int> GetNextLessonOrderIndexAsync(Guid courseId, CancellationToken cancellationToken) =>
        Task.FromResult(Lessons.Values.Where(l => l.CourseId == courseId).Select(l => l.OrderIndex + 1).DefaultIfEmpty(0).Max());

    public Task<int> GetNextQuestionOrderIndexAsync(Guid lessonId, CancellationToken cancellationToken) =>
        Task.FromResult(Questions.Values.Where(q => q.LessonId == lessonId).Select(q => q.OrderIndex + 1).DefaultIfEmpty(0).Max());

    public void Add(Course course) => Courses[course.Id] = course;

    public void Add(Lesson lesson) => Lessons[lesson.Id] = lesson;

    public void Add(Question question) => Questions[question.Id] = question;

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailNextSaveWithUniqueViolation)
        {
            FailNextSaveWithUniqueViolation = false;
            throw new UniqueConstraintViolationException("ix_courses_code", new InvalidOperationException("duplicate"));
        }

        SaveCount++;
        return Task.CompletedTask;
    }

    // ---- Test data ----------------------------------------------------------------------------------------

    public Course AddCourse(string code = "EN_CEFR_A1", TargetLanguage language = TargetLanguage.English, LanguageLevel level = LanguageLevel.A1)
    {
        var course = new Course(code, language, level);
        Courses[course.Id] = course;
        return course;
    }

    public Lesson AddLesson(Course course, int orderIndex = 0, bool published = false)
    {
        var lesson = new Lesson(course.Id, orderIndex, LessonType.Standard);
        if (published)
            lesson.Publish();
        Lessons[lesson.Id] = lesson;
        return lesson;
    }

    public Question AddQuestion(Lesson lesson, int orderIndex = 0)
    {
        var question = new Question(lesson.Id, orderIndex, QuestionType.MultipleChoice, "hello");
        Questions[question.Id] = question;
        return question;
    }
}

/// <summary>Returns what it is told and remembers what it was asked, to check that queries pass their arguments through.</summary>
internal sealed class FakeContentReadModel : IContentReadModel
{
    public IReadOnlyList<CourseSummaryDto> Courses { get; set; } = [];

    public CourseDetailDto? Detail { get; set; }

    public IReadOnlyList<LessonDto>? Lessons { get; set; }

    public LessonPreviewDto? Preview { get; set; }

    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<CourseSummaryDto>> GetPublishedCoursesAsync(BaseLanguage baseLanguage, TargetLanguage targetLanguage, CancellationToken cancellationToken)
    {
        Calls.Add($"published:{baseLanguage}:{targetLanguage}");
        return Task.FromResult(Courses);
    }

    public Task<IReadOnlyList<CourseSummaryDto>> GetAllCoursesAsync(TargetLanguage? targetLanguage, BaseLanguage baseLanguage, CancellationToken cancellationToken)
    {
        Calls.Add($"all:{targetLanguage?.ToString() ?? "any"}:{baseLanguage}");
        return Task.FromResult(Courses);
    }

    public Task<CourseDetailDto?> GetCourseDetailAsync(Guid courseId, BaseLanguage baseLanguage, bool includeUnpublished, CancellationToken cancellationToken)
    {
        Calls.Add($"detail:{courseId}:{baseLanguage}:{includeUnpublished}");
        return Task.FromResult(Detail);
    }

    public Task<IReadOnlyList<LessonDto>?> GetLessonsAsync(Guid courseId, BaseLanguage baseLanguage, bool includeUnpublished, CancellationToken cancellationToken)
    {
        Calls.Add($"lessons:{courseId}:{baseLanguage}:{includeUnpublished}");
        return Task.FromResult(Lessons);
    }

    public Task<LessonPreviewDto?> PreviewLessonAsync(Guid lessonId, BaseLanguage baseLanguage, CancellationToken cancellationToken)
    {
        Calls.Add($"preview:{lessonId}:{baseLanguage}");
        return Task.FromResult(Preview);
    }
}
