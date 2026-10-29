using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.Learning.Entities;

/// <summary>A learner's overall progress through one course. Keyed by (UserId, CourseId).</summary>
public class UserCourseProgress
{
    // Required by EF Core.
    private UserCourseProgress() { }

    public UserCourseProgress(Guid userId, Guid courseId, int totalLessonsCount)
    {
        UserId = Guard.RequiredId(userId, nameof(userId));
        CourseId = Guard.RequiredId(courseId, nameof(courseId));
        TotalLessonsCount = Guard.NonNegative(totalLessonsCount, nameof(totalLessonsCount));
        CompletedLessonsCount = 0;
        IsCompleted = totalLessonsCount == 0;
        LastAccessedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }

    /// <summary>Id of a course in the Content module; no database FK across module schemas.</summary>
    public Guid CourseId { get; private set; }

    public int CompletedLessonsCount { get; private set; }
    public int TotalLessonsCount { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateTimeOffset LastAccessedAt { get; private set; }

    /// <summary>
    /// Sets the counts from facts rather than adding one, so recording the same completion twice (a double click,
    /// a retry) cannot over-count. The completed count never exceeds the total.
    /// </summary>
    public void Update(int completedLessonsCount, int totalLessonsCount)
    {
        TotalLessonsCount = Guard.NonNegative(totalLessonsCount, nameof(totalLessonsCount));
        CompletedLessonsCount = Math.Min(Guard.NonNegative(completedLessonsCount, nameof(completedLessonsCount)), TotalLessonsCount);
        IsCompleted = TotalLessonsCount > 0 && CompletedLessonsCount >= TotalLessonsCount;
        LastAccessedAt = DateTimeOffset.UtcNow;
    }

    public void RecordLessonCompleted()
    {
        if (CompletedLessonsCount < TotalLessonsCount)
            CompletedLessonsCount++;

        IsCompleted = CompletedLessonsCount >= TotalLessonsCount;
        LastAccessedAt = DateTimeOffset.UtcNow;
    }
}
