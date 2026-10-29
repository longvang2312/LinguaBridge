using LinguaBridge.Domain.Enums;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LinguaBridge.Infrastructure.Persistence.Converters;

/// <summary>Stores <see cref="TargetLanguage"/> as its ISO code (<c>en</c>, <c>zh</c>).</summary>
internal sealed class TargetLanguageConverter()
    : ValueConverter<TargetLanguage, string>(v => v.ToCode(), v => LanguageCodeParser.ParseTargetLanguage(v))
{
    public const int MaxLength = 10;
}

/// <summary>Stores <see cref="BaseLanguage"/> as its ISO code (<c>vi</c>, <c>en</c>).</summary>
internal sealed class BaseLanguageConverter()
    : ValueConverter<BaseLanguage, string>(v => v.ToCode(), v => LanguageCodeParser.ParseBaseLanguage(v))
{
    public const int MaxLength = 10;
}

/// <summary>Stores <see cref="LanguageLevel"/> in upper case (<c>A1</c>, <c>HSK1</c>).</summary>
internal sealed class LanguageLevelConverter()
    : ValueConverter<LanguageLevel, string>(v => v.ToString().ToUpperInvariant(), v => LanguageCodeParser.ParseLevel(v))
{
    public const int MaxLength = 20;
}

/// <summary>Reverse of the Domain's <c>ToCode</c> mappings. Unknown values fail loudly rather than reading as a default.</summary>
internal static class LanguageCodeParser
{
    public static TargetLanguage ParseTargetLanguage(string code) => code switch
    {
        "en" => TargetLanguage.English,
        "zh" => TargetLanguage.Chinese,
        _ => throw new InvalidOperationException($"Unknown target language code '{code}'."),
    };

    public static BaseLanguage ParseBaseLanguage(string code) => code switch
    {
        "vi" => BaseLanguage.Vietnamese,
        "en" => BaseLanguage.English,
        _ => throw new InvalidOperationException($"Unknown base language code '{code}'."),
    };

    public static LanguageLevel ParseLevel(string code) =>
        !int.TryParse(code, out _) && Enum.TryParse<LanguageLevel>(code, ignoreCase: true, out var level) && Enum.IsDefined(level)
            ? level
            : throw new InvalidOperationException($"Unknown language level '{code}'.");
}
