using LinguaBridge.Domain.Enums;

namespace LinguaBridge.Modules.Content;

/// <summary>
/// Chooses which translation to show: the requested language, else English (the common denominator of the
/// content team), else whatever exists. Content is never hidden just because it lacks one language.
/// </summary>
public static class TranslationResolver
{
    public static T? Pick<T>(IEnumerable<T> translations, BaseLanguage requested, Func<T, BaseLanguage> languageOf)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(languageOf);

        var all = translations.ToList();
        return all.FirstOrDefault(t => languageOf(t) == requested)
            ?? all.FirstOrDefault(t => languageOf(t) == BaseLanguage.English)
            ?? all.OrderBy(t => languageOf(t)).FirstOrDefault();
    }
}
