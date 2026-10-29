namespace LinguaBridge.Domain.Enums;

/// <summary>Maps the language enums to the codes used in storage and in the API.</summary>
public static class LanguageCodes
{
    public static string ToCode(this TargetLanguage language) => language switch
    {
        TargetLanguage.English => "en",
        TargetLanguage.Chinese => "zh",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    public static string ToCode(this BaseLanguage language) => language switch
    {
        BaseLanguage.Vietnamese => "vi",
        BaseLanguage.English => "en",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    public static string ToCode(this UiLocale locale) => locale switch
    {
        UiLocale.ViVn => "vi-VN",
        UiLocale.EnUs => "en-US",
        UiLocale.ZhCn => "zh-CN",
        UiLocale.JaJp => "ja-JP",
        _ => throw new ArgumentOutOfRangeException(nameof(locale), locale, null),
    };

    /// <summary>CEFR levels belong to English, HSK levels to Chinese.</summary>
    public static bool AppliesTo(this LanguageLevel level, TargetLanguage language) => language switch
    {
        TargetLanguage.English => level is >= LanguageLevel.A1 and <= LanguageLevel.C2,
        TargetLanguage.Chinese => level is >= LanguageLevel.Hsk1 and <= LanguageLevel.Hsk6,
        _ => false,
    };
}
