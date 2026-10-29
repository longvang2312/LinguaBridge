using System.Globalization;
using System.Text;

namespace LinguaBridge.Modules.Learning.Grading;

/// <summary>
/// Compares a typed answer with the expected text. Forgiving about things that are not the language being
/// tested: case, surrounding whitespace and punctuation, repeated spaces, and full-width versus half-width
/// forms (so a Chinese keyboard's "你好！" matches "你好"). Not forgiving about spelling or words.
/// </summary>
public static class AnswerMatcher
{
    public static bool Matches(string? answer, string? expected)
    {
        var normalisedAnswer = Normalise(answer);
        return normalisedAnswer.Length > 0 && string.Equals(normalisedAnswer, Normalise(expected), StringComparison.Ordinal);
    }

    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // NFKC folds full-width letters, digits and punctuation into their ordinary forms and composes accents.
        var folded = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

        var words = new StringBuilder(folded.Length);
        var pendingSpace = false;
        foreach (var character in folded)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = words.Length > 0;
                continue;
            }

            if (pendingSpace)
                words.Append(' ');
            pendingSpace = false;
            words.Append(character);
        }

        return TrimPunctuation(words.ToString());
    }

    /// <summary>Only the ends: punctuation inside the sentence (commas, apostrophes) is part of the answer.</summary>
    private static string TrimPunctuation(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsDecoration(text[start]))
            start++;
        while (end > start && IsDecoration(text[end - 1]))
            end--;
        return text[start..end].Trim();
    }

    private static bool IsDecoration(char character) =>
        char.IsWhiteSpace(character) || char.IsPunctuation(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.OtherSymbol;
}
