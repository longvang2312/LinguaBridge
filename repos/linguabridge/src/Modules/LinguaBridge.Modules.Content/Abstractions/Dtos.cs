namespace LinguaBridge.Modules.Content.Abstractions;

/// <summary>A translation as stored; <c>Language</c> is the base-language code (<c>vi</c>, <c>en</c>).</summary>
public sealed record CourseTranslationDto(string Language, string Title, string? Description);

/// <param name="Title">In <paramref name="TranslationLanguage"/>; null when the course has no translation at all.</param>
/// <param name="TranslationLanguage">The language <c>Title</c>/<c>Description</c> are actually in: the requested one, else English, else any.</param>
public sealed record CourseSummaryDto(
    Guid Id,
    string Code,
    string TargetLanguage,
    string Level,
    string Status,
    int Version,
    string? ThumbnailUrl,
    string? Title,
    string? Description,
    string? TranslationLanguage,
    DateTimeOffset UpdatedAt);

public sealed record CourseDetailDto(CourseSummaryDto Course, IReadOnlyList<CourseTranslationDto> Translations);

public sealed record LessonDto(
    Guid Id,
    Guid CourseId,
    int OrderIndex,
    string LessonType,
    string Status,
    int XpReward,
    int Version,
    string? Title,
    string? GuideText,
    string? TranslationLanguage);

public sealed record QuestionOptionDto(Guid Id, int OrderIndex, string OptionText, string? PinyinOrIpa, bool IsCorrect);

public sealed record QuestionDto(
    Guid Id,
    int OrderIndex,
    string QuestionType,
    string? AudioPromptUrl,
    string TargetText,
    string? PinyinOrIpa,
    string? GrammarNotes,
    string? PromptTranslation,
    string? Explanation,
    string? TranslationLanguage,
    IReadOnlyList<QuestionOptionDto> Options);

/// <summary>What an admin sees when previewing a lesson, including its correct answers.</summary>
public sealed record LessonPreviewDto(LessonDto Lesson, IReadOnlyList<QuestionDto> Questions);
