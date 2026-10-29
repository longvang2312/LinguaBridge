using System.Text.Json;

namespace LinguaBridge.Modules.AiTutor.Responses;

/// <param name="IsStructured">False when the model's output was not the requested JSON and the raw text became the reply.</param>
public sealed record AiTutorResponse(string ReplyText, string? PinyinOrIpa, string? Translation, string? GrammarTips, bool IsStructured);

/// <summary>
/// Reads the model's output. The model is asked for <c>{reply_text, pinyin_or_ipa, translation, grammar_tips}</c>;
/// when it answers in plain text (or the JSON has no usable reply) the whole text is the reply and the other fields stay empty,
/// so the learner still gets an answer.
/// </summary>
public static class AiTutorResponseParser
{
    public static AiTutorResponse Parse(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        return TryParseJson(StripCodeFence(text)) ?? new AiTutorResponse(text, null, null, null, IsStructured: false);
    }

    private static AiTutorResponse? TryParseJson(string json)
    {
        if (!json.StartsWith('{'))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var reply = Read(document.RootElement, "reply_text");
            if (reply is null)
                return null;

            return new AiTutorResponse(
                reply,
                Read(document.RootElement, "pinyin_or_ipa"),
                Read(document.RootElement, "translation"),
                Read(document.RootElement, "grammar_tips"),
                IsStructured: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string, or an array of strings joined by lines (models sometimes list the tips); blank counts as absent.</summary>
    private static string? Read(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join('\n', value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Models often wrap JSON in a ```json fence even when told not to.</summary>
    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstLineEnd = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineEnd < 0 || lastFence <= firstLineEnd)
            return text;

        return text[(firstLineEnd + 1)..lastFence].Trim();
    }
}
