using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Content.Abstractions;
using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LinguaBridge.Infrastructure.Persistence.Content;

internal sealed class ContentRepository(LinguaBridgeDbContext db) : IContentRepository
{
    public Task<bool> CourseCodeExistsAsync(string code, CancellationToken cancellationToken) =>
        db.Courses.AnyAsync(c => c.Code == code, cancellationToken);

    public Task<Course?> GetCourseAsync(Guid courseId, CancellationToken cancellationToken) =>
        db.Courses.Include(c => c.Translations).SingleOrDefaultAsync(c => c.Id == courseId, cancellationToken);

    public Task<Lesson?> GetLessonAsync(Guid lessonId, CancellationToken cancellationToken) =>
        db.Lessons.Include(l => l.Translations).SingleOrDefaultAsync(l => l.Id == lessonId, cancellationToken);

    public Task<Question?> GetQuestionAsync(Guid questionId, CancellationToken cancellationToken) =>
        db.Questions
            .Include(q => q.Options)
            .Include(q => q.Translations)
            .AsSplitQuery()
            .SingleOrDefaultAsync(q => q.Id == questionId, cancellationToken);

    public Task<bool> HasPublishedLessonAsync(Guid courseId, CancellationToken cancellationToken) =>
        db.Lessons.AnyAsync(l => l.CourseId == courseId && l.Status == ContentStatus.Published, cancellationToken);

    public async Task<int> GetNextLessonOrderIndexAsync(Guid courseId, CancellationToken cancellationToken) =>
        (await db.Lessons.Where(l => l.CourseId == courseId).MaxAsync(l => (int?)l.OrderIndex, cancellationToken) ?? -1) + 1;

    public async Task<int> GetNextQuestionOrderIndexAsync(Guid lessonId, CancellationToken cancellationToken) =>
        (await db.Questions.Where(q => q.LessonId == lessonId).MaxAsync(q => (int?)q.OrderIndex, cancellationToken) ?? -1) + 1;

    public void Add(Course course) => db.Courses.Add(course);

    public void Add(Lesson lesson) => db.Lessons.Add(lesson);

    public void Add(Question question) => db.Questions.Add(question);

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
