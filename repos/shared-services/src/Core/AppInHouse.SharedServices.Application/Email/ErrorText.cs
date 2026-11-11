using System.Text;

namespace AppInHouse.SharedServices.Application.Email;

/// <summary>
/// Makes an error message safe to store and to show: one short line, with any secret that happens to be in it removed. Provider
/// libraries sometimes echo what they were given (a URL with a key, a server's reply), so every message passes through here before it
/// reaches a response, a delivery log or the page.
/// </summary>
public static class ErrorText
{
    public const int MaxLength = 200;

    public static string Clean(string? text, params string?[] secrets)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Unknown error.";

        var cleaned = text;
        foreach (var secret in secrets)
        {
            // Very short "secrets" would make the message unreadable ("a" everywhere); the real ones are long.
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                cleaned = cleaned.Replace(secret, "••••", StringComparison.Ordinal);
        }

        var line = new StringBuilder(cleaned.Length);
        var lastWasSpace = false;
        foreach (var c in cleaned)
        {
            var space = char.IsControl(c) || char.IsWhiteSpace(c);
            if (space && lastWasSpace) continue;
            line.Append(space ? ' ' : c);
            lastWasSpace = space;
        }

        var result = line.ToString().Trim();
        return result.Length <= MaxLength ? result : result[..(MaxLength - 1)] + "…";
    }
}
