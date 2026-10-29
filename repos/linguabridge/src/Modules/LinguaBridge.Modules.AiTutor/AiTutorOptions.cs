using Microsoft.Extensions.Configuration;

namespace LinguaBridge.Modules.AiTutor;

/// <summary>Settings from the <c>AiTutor</c> configuration section.</summary>
public sealed class AiTutorOptions
{
    public const string SectionName = "AiTutor";

    /// <summary>AI requests a learner without a paid plan may make per UTC day.</summary>
    public int FreeDailyRequests { get; init; } = 10;

    /// <summary>How many earlier messages are replayed to the model for context.</summary>
    public int HistoryMessages { get; init; } = 10;

    /// <summary>Longest learner message accepted, in characters; bounds what one request can cost.</summary>
    public int MaxMessageLength { get; init; } = 1000;

    /// <summary>Model to ask for; empty uses the provider's own default (<c>OpenAI:Model</c>).</summary>
    public string ModelId { get; init; } = string.Empty;

    public static AiTutorOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaults = new AiTutorOptions();
        return new AiTutorOptions
        {
            FreeDailyRequests = ReadInt(configuration, nameof(FreeDailyRequests), defaults.FreeDailyRequests, minimum: 0),
            HistoryMessages = ReadInt(configuration, nameof(HistoryMessages), defaults.HistoryMessages, minimum: 0),
            MaxMessageLength = ReadInt(configuration, nameof(MaxMessageLength), defaults.MaxMessageLength, minimum: 1),
            ModelId = configuration[$"{SectionName}:{nameof(ModelId)}"]?.Trim() ?? string.Empty,
        };
    }

    private static int ReadInt(IConfiguration configuration, string name, int fallback, int minimum)
    {
        var key = $"{SectionName}:{name}";
        var text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        if (!int.TryParse(text, out var value) || value < minimum)
            throw new InvalidOperationException($"Configuration '{key}' must be an integer of at least {minimum}, but is '{text}'.");

        return value;
    }
}
