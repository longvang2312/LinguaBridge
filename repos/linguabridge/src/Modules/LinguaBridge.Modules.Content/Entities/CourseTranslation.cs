using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Content.Entities;

/// <summary>Course title and description in one base language. Keyed by (CourseId, BaseLanguage).</summary>
public class CourseTranslation
{
    public const int TitleMaxLength = 255;

    // Required by EF Core.
    private CourseTranslation() { }

    internal CourseTranslation(Guid courseId, BaseLanguage baseLanguage, string title, string? description)
    {
        CourseId = Guard.RequiredId(courseId, nameof(courseId));
        BaseLanguage = baseLanguage;
        Update(title, description);
    }

    public Guid CourseId { get; private set; }
    public BaseLanguage BaseLanguage { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    internal void Update(string title, string? description)
    {
        Title = Guard.Required(title, TitleMaxLength, nameof(title));
        Description = Guard.Optional(description, int.MaxValue, nameof(description));
    }
}
