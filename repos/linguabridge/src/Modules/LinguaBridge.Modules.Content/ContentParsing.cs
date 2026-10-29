using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.Content.Entities;

namespace LinguaBridge.Modules.Content;

/// <summary>Turns the codes used on the wire (<c>en</c>, <c>vi</c>, <c>HSK1</c>, <c>Draft</c>) into domain values. Case-insensitive, and strict: numbers and unknown names are rejected.</summary>
public static class ContentParsing
{
    public static bool TryParseTargetLanguage(string? code, out TargetLanguage language)
    {
        (var ok, language) = Normalise(code) switch
        {
            "en" => (true, TargetLanguage.English),
            "zh" => (true, TargetLanguage.Chinese),
            _ => (false, default(TargetLanguage)),
        };
        return ok;
    }

    public static bool TryParseBaseLanguage(string? code, out BaseLanguage language)
    {
        (var ok, language) = Normalise(code) switch
        {
            "vi" => (true, BaseLanguage.Vietnamese),
            "en" => (true, BaseLanguage.English),
            _ => (false, default(BaseLanguage)),
        };
        return ok;
    }

    /// <summary>Accepts <c>A1</c> to <c>C2</c> and <c>HSK1</c> to <c>HSK6</c>.</summary>
    public static bool TryParseLevel(string? code, out LanguageLevel level) => TryParseName(code, out level);

    public static bool TryParseLessonType(string? name, out LessonType type) => TryParseName(name, out type);

    public static bool TryParseQuestionType(string? name, out QuestionType type) => TryParseName(name, out type);

    private static string? Normalise(string? code) => code?.Trim().ToLowerInvariant();

    /// <summary>
    /// Matches the exact member name. <c>Enum.TryParse</c> is not used on purpose: it also accepts numbers and
    /// comma-separated lists, so "A1,B1" would parse as a level.
    /// </summary>
    private static bool TryParseName<TEnum>(string? name, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        var match = Enum.GetNames<TEnum>().FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        value = Enum.Parse<TEnum>(match);
        return true;
    }
}
