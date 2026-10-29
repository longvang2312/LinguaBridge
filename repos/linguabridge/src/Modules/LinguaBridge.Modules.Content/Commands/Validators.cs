using FluentValidation;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.Modules.Content.Commands;

/// <summary>Rules every command shares. They check shape only; anything that needs the database lives in the handler.</summary>
internal static class ValidationRules
{
    /// <summary>The domain trims before checking length, so the rule does too.</summary>
    public static IRuleBuilderOptions<T, string?> RequiredText<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.Must(value => !string.IsNullOrWhiteSpace(value)).WithMessage("'{PropertyName}' must not be empty.")
            .Must(value => value is null || value.Trim().Length <= maxLength).WithMessage($"'{{PropertyName}}' must not exceed {maxLength} characters.");

    public static IRuleBuilderOptions<T, string?> OptionalText<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.Must(value => value is null || value.Trim().Length <= maxLength).WithMessage($"'{{PropertyName}}' must not exceed {maxLength} characters.");

    public static IRuleBuilderOptions<T, string?> BaseLanguageCode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(value => ContentParsing.TryParseBaseLanguage(value, out _)).WithMessage("'{PropertyName}' must be 'vi' or 'en'.");
}

public sealed class CreateCourseCommandValidator : AbstractValidator<CreateCourseCommand>
{
    public CreateCourseCommandValidator()
    {
        RuleFor(c => c.Code).RequiredText(Course.CodeMaxLength);
        RuleFor(c => c.TargetLanguage)
            .Must(code => ContentParsing.TryParseTargetLanguage(code, out _)).WithMessage("'Target Language' must be 'en' or 'zh'.");
        RuleFor(c => c.Level)
            .Must(code => ContentParsing.TryParseLevel(code, out _)).WithMessage("'Level' must be A1-C2 or HSK1-HSK6.");
        RuleFor(c => c.ThumbnailUrl).OptionalText(Course.ThumbnailUrlMaxLength);

        // Only meaningful when both parsed: otherwise the rules above already say what is wrong.
        RuleFor(c => c.Level)
            .Must((c, level) => !ContentParsing.TryParseTargetLanguage(c.TargetLanguage, out var language)
                || !ContentParsing.TryParseLevel(level, out var parsed)
                || parsed.AppliesTo(language))
            .WithMessage("'Level' does not belong to the target language (CEFR levels are for 'en', HSK levels for 'zh').");
    }
}

public sealed class UpdateCourseCommandValidator : AbstractValidator<UpdateCourseCommand>
{
    public UpdateCourseCommandValidator()
    {
        RuleFor(c => c.CourseId).NotEmpty();
        RuleFor(c => c.Level).Must(code => ContentParsing.TryParseLevel(code, out _)).WithMessage("'Level' must be A1-C2 or HSK1-HSK6.");
        RuleFor(c => c.ThumbnailUrl).OptionalText(Course.ThumbnailUrlMaxLength);
    }
}

public sealed class AddCourseTranslationCommandValidator : AbstractValidator<AddCourseTranslationCommand>
{
    public AddCourseTranslationCommandValidator()
    {
        RuleFor(c => c.CourseId).NotEmpty();
        RuleFor(c => c.BaseLanguage).BaseLanguageCode();
        RuleFor(c => c.Title).RequiredText(CourseTranslation.TitleMaxLength);
    }
}

public sealed class UpdateCourseTranslationCommandValidator : AbstractValidator<UpdateCourseTranslationCommand>
{
    public UpdateCourseTranslationCommandValidator()
    {
        RuleFor(c => c.CourseId).NotEmpty();
        RuleFor(c => c.BaseLanguage).BaseLanguageCode();
        RuleFor(c => c.Title).RequiredText(CourseTranslation.TitleMaxLength);
    }
}

public sealed class CreateLessonCommandValidator : AbstractValidator<CreateLessonCommand>
{
    public CreateLessonCommandValidator()
    {
        RuleFor(c => c.CourseId).NotEmpty();
        RuleFor(c => c.LessonType)
            .Must(name => ContentParsing.TryParseLessonType(name, out _)).WithMessage("'Lesson Type' must be Standard, Vocabulary, Pinyin or Grammar.");
        RuleFor(c => c.OrderIndex).GreaterThanOrEqualTo(0).When(c => c.OrderIndex.HasValue);
        RuleFor(c => c.XpReward).GreaterThanOrEqualTo(0).When(c => c.XpReward.HasValue);
    }
}

public sealed class AddLessonTranslationCommandValidator : AbstractValidator<AddLessonTranslationCommand>
{
    public AddLessonTranslationCommandValidator()
    {
        RuleFor(c => c.LessonId).NotEmpty();
        RuleFor(c => c.BaseLanguage).BaseLanguageCode();
        RuleFor(c => c.Title).RequiredText(LessonTranslation.TitleMaxLength);
    }
}

public sealed class AddQuestionCommandValidator : AbstractValidator<AddQuestionCommand>
{
    public AddQuestionCommandValidator()
    {
        RuleFor(c => c.LessonId).NotEmpty();
        RuleFor(c => c.QuestionType)
            .Must(name => ContentParsing.TryParseQuestionType(name, out _)).WithMessage("'Question Type' must be MultipleChoice, Translate, FillBlank or Pronounce.");
        RuleFor(c => c.TargetText).RequiredText(int.MaxValue);
        RuleFor(c => c.OrderIndex).GreaterThanOrEqualTo(0).When(c => c.OrderIndex.HasValue);
        RuleFor(c => c.PinyinOrIpa).OptionalText(Question.PinyinOrIpaMaxLength);
        RuleFor(c => c.AudioPromptUrl).OptionalText(Question.AudioPromptUrlMaxLength);

        RuleForEach(c => c.Translations).ChildRules(translation =>
        {
            translation.RuleFor(t => t.BaseLanguage).BaseLanguageCode();
            translation.RuleFor(t => t.PromptTranslation).RequiredText(int.MaxValue);
        });
        RuleFor(c => c.Translations)
            .Must(translations => translations is null
                || translations.Select(t => t.BaseLanguage?.Trim().ToLowerInvariant()).Distinct().Count() == translations.Count)
            .WithMessage("'Translations' must not contain the same language twice.");
    }
}

public sealed class AddQuestionOptionCommandValidator : AbstractValidator<AddQuestionOptionCommand>
{
    public AddQuestionOptionCommandValidator()
    {
        RuleFor(c => c.QuestionId).NotEmpty();
        RuleFor(c => c.OptionText).RequiredText(int.MaxValue);
        RuleFor(c => c.PinyinOrIpa).OptionalText(QuestionOption.PinyinOrIpaMaxLength);
    }
}
