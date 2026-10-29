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

    /// <summary>Changes the level and thumbnail. Code and target language are identity and never change. Not allowed once archived.</summary>
    public void UpdateDetails(LanguageLevel level, string? thumbnailUrl)
    {
        EnsureNotArchived();
        if (!level.AppliesTo(TargetLanguage))
            throw new ArgumentException($"Level {level} does not belong to target language {TargetLanguage}.", nameof(level));

        Level = level;
        ThumbnailUrl = Guard.Optional(thumbnailUrl, ThumbnailUrlMaxLength, nameof(thumbnailUrl));
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Draft to Review.</summary>
    public void SubmitForReview()
    {
        if (Status != ContentStatus.Draft)
            throw new InvalidOperationException($"Only a draft course can be submitted for review (status is {Status}).");

        Status = ContentStatus.Review;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Draft or Review to Published. Whether the course has anything to show is the caller's rule: it needs the lessons.</summary>
    public void Publish()
    {
        if (Status is not (ContentStatus.Draft or ContentStatus.Review))
            throw new InvalidOperationException($"Only a draft or in-review course can be published (status is {Status}).");

        Status = ContentStatus.Published;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Takes the course out of circulation from any other state. Archived is final.</summary>
    public void Archive()
    {
        EnsureNotArchived();

        Status = ContentStatus.Archived;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool IsArchived => Status == ContentStatus.Archived;

    private void EnsureNotArchived()
    {
        if (IsArchived)
            throw new InvalidOperationException("An archived course cannot be changed.");
    }

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
