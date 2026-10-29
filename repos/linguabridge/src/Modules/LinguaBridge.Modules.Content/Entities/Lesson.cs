using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Content.Entities;

/// <summary>One lesson of a course. Its own aggregate so a course is never loaded with its whole curriculum.</summary>
public class Lesson : AggregateRoot
{
    public const int DefaultXpReward = 20;

    private readonly List<LessonTranslation> _translations = [];

    // Required by EF Core.
    private Lesson() { }

    public Lesson(Guid courseId, int orderIndex, LessonType lessonType, int xpReward = DefaultXpReward)
    {
        CourseId = Guard.RequiredId(courseId, nameof(courseId));
        OrderIndex = Guard.NonNegative(orderIndex, nameof(orderIndex));
        LessonType = lessonType;
        Status = ContentStatus.Draft;
        XpReward = Guard.NonNegative(xpReward, nameof(xpReward));
        Version = 1;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid CourseId { get; private set; }
    public int OrderIndex { get; private set; }
    public LessonType LessonType { get; private set; }
    public ContentStatus Status { get; private set; }
    public int XpReward { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<LessonTranslation> Translations => _translations.AsReadOnly();

    /// <summary>Adds the translation for <paramref name="baseLanguage"/>, or replaces the existing one.</summary>
    public void SetTranslation(BaseLanguage baseLanguage, string title, string? guideText = null)
    {
        var existing = _translations.SingleOrDefault(t => t.BaseLanguage == baseLanguage);
        if (existing is null)
            _translations.Add(new LessonTranslation(Id, baseLanguage, title, guideText));
        else
            existing.Update(title, guideText);
    }
}
