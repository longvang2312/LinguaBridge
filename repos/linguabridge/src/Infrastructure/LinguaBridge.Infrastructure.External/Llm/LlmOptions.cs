using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace LinguaBridge.Infrastructure.External.Llm;

/// <summary>USD per million tokens, used to estimate what a call cost for the ledger.</summary>
public sealed record TokenPricing(decimal InputUsdPerMillion, decimal OutputUsdPerMillion)
{
    public decimal Cost(int promptTokens, int completionTokens) =>
        Math.Round((promptTokens * InputUsdPerMillion + completionTokens * OutputUsdPerMillion) / 1_000_000m, 6);
}

/// <summary>Settings from the <c>OpenAI</c> section: <c>ApiKey</c>, <c>Model</c> (default gpt-4o-mini) and a few tuning knobs.</summary>
public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-4o-mini";
    public string BaseUrl { get; init; } = "https://api.openai.com/v1/";

    /// <summary>Upper bound on the reply length, which also bounds the cost of one call.</summary>
    public int MaxOutputTokens { get; init; } = 600;

    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Public list price of gpt-4o-mini; override for another model.</summary>
    public TokenPricing Pricing { get; init; } = new(0.15m, 0.60m);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public static OpenAiOptions From(IConfiguration configuration)
    {
        var defaults = new OpenAiOptions();
        return new OpenAiOptions
        {
            ApiKey = LlmConfig.Text(configuration, SectionName, nameof(ApiKey)) ?? string.Empty,
            Model = LlmConfig.Text(configuration, SectionName, nameof(Model)) ?? defaults.Model,
            BaseUrl = LlmConfig.Text(configuration, SectionName, nameof(BaseUrl)) ?? defaults.BaseUrl,
            MaxOutputTokens = LlmConfig.PositiveInt(configuration, SectionName, nameof(MaxOutputTokens), defaults.MaxOutputTokens),
            TimeoutSeconds = LlmConfig.PositiveInt(configuration, SectionName, nameof(TimeoutSeconds), defaults.TimeoutSeconds),
            Pricing = LlmConfig.Pricing(configuration, SectionName, defaults.Pricing),
        };
    }
}

/// <summary>
/// Optional fallback from the <c>AzureOpenAI</c> section. Used only when <c>Endpoint</c>, <c>ApiKey</c> and
/// <c>Deployment</c> are all set.
/// </summary>
public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The name of the deployed model (Azure addresses models by deployment, not by model id).</summary>
    public string Deployment { get; init; } = string.Empty;

    public string ApiVersion { get; init; } = "2024-06-01";
    public int MaxOutputTokens { get; init; } = 600;
    public int TimeoutSeconds { get; init; } = 30;
    public TokenPricing Pricing { get; init; } = new(0.15m, 0.60m);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Deployment);

    public static AzureOpenAiOptions From(IConfiguration configuration)
    {
        var defaults = new AzureOpenAiOptions();
        return new AzureOpenAiOptions
        {
            Endpoint = LlmConfig.Text(configuration, SectionName, nameof(Endpoint)) ?? string.Empty,
            ApiKey = LlmConfig.Text(configuration, SectionName, nameof(ApiKey)) ?? string.Empty,
            Deployment = LlmConfig.Text(configuration, SectionName, nameof(Deployment)) ?? string.Empty,
            ApiVersion = LlmConfig.Text(configuration, SectionName, nameof(ApiVersion)) ?? defaults.ApiVersion,
            MaxOutputTokens = LlmConfig.PositiveInt(configuration, SectionName, nameof(MaxOutputTokens), defaults.MaxOutputTokens),
            TimeoutSeconds = LlmConfig.PositiveInt(configuration, SectionName, nameof(TimeoutSeconds), defaults.TimeoutSeconds),
            Pricing = LlmConfig.Pricing(configuration, SectionName, defaults.Pricing),
        };
    }
}

internal static class LlmConfig
{
    public static string? Text(IConfiguration configuration, string section, string name)
    {
        var value = configuration[$"{section}:{name}"];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static int PositiveInt(IConfiguration configuration, string section, string name, int fallback)
    {
        var text = Text(configuration, section, name);
        if (text is null)
            return fallback;

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"Configuration '{section}:{name}' must be a positive integer, but is '{text}'.");
    }

    public static TokenPricing Pricing(IConfiguration configuration, string section, TokenPricing fallback) => new(
        Price(configuration, section, nameof(TokenPricing.InputUsdPerMillion), fallback.InputUsdPerMillion),
        Price(configuration, section, nameof(TokenPricing.OutputUsdPerMillion), fallback.OutputUsdPerMillion));

    private static decimal Price(IConfiguration configuration, string section, string name, decimal fallback)
    {
        var text = Text(configuration, section, name);
        if (text is null)
            return fallback;

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : throw new InvalidOperationException($"Configuration '{section}:{name}' must be a non-negative number, but is '{text}'.");
    }
}
