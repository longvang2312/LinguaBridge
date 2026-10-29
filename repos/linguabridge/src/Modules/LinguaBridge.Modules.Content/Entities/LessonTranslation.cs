using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Content.Entities;

/// <summary>Lesson title and theory guide in one base language. Keyed by (LessonId, BaseLanguage).</summary>
public class LessonTranslation
{
    public const int TitleMaxLength = 255;

    // Required by EF Core.
    private LessonTranslation() { }

    internal LessonTranslation(Guid lessonId, BaseLanguage baseLanguage, string title, string? guideText)
    {
        LessonId = Guard.RequiredId(lessonId, nameof(lessonId));
        BaseLanguage = baseLanguage;
        Update(title, guideText);
    }

    public Guid LessonId { get; private set; }
    public BaseLanguage BaseLanguage { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? GuideText { get; private set; }

    internal void Update(string title, string? guideText)
    {
        Title = Guard.Required(title, TitleMaxLength, nameof(title));
        GuideText = Guard.Optional(guideText, int.MaxValue, nameof(guideText));
    }
}
