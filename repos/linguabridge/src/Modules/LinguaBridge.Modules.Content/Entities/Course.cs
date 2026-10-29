using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Content.Entities;

/// <summary>A course for one target language at one level (e.g. <c>EN_CEFR_A1</c>, <c>ZH_HSK_1</c>), with per-base-language titles.</summary>
public class Course : AggregateRoot
{
    public const int CodeMaxLength = 50;
    public const int ThumbnailUrlMaxLength = 500;

    private readonly List<CourseTranslation> _translations = [];

    // Required by EF Core.
    private Course() { }

    public Course(string code, TargetLanguage targetLanguage, LanguageLevel level, string? thumbnailUrl = null)
    {
        if (!level.AppliesTo(targetLanguage))
            throw new ArgumentException($"Level {level} does not belong to target language {targetLanguage}.", nameof(level));

        Code = Guard.Required(code, CodeMaxLength, nameof(code));
        TargetLanguage = targetLanguage;
        Level = level;
        Status = ContentStatus.Draft;
        Version = 1;
        ThumbnailUrl = Guard.Optional(thumbnailUrl, ThumbnailUrlMaxLength, nameof(thumbnailUrl));
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public string Code { get; private set; } = string.Empty;
    public TargetLanguage TargetLanguage { get; private set; }
    public LanguageLevel Level { get; private set; }
    public ContentStatus Status { get; private set; }
    public int Version { get; private set; }
    public string? ThumbnailUrl { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<CourseTranslation> Translations => _translations.AsReadOnly();

    /// <summary>Adds the translation for <paramref name="baseLanguage"/>, or replaces the existing one.</summary>
    public void SetTranslation(BaseLanguage baseLanguage, string title, string? description = null)
    {
        var existing = _translations.SingleOrDefault(t => t.BaseLanguage == baseLanguage);
        if (existing is null)
            _translations.Add(new CourseTranslation(Id, baseLanguage, title, description));
        else
            existing.Update(title, description);

        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
