using System.Net.Http.Headers;
using LinguaBridge.Modules.AiTutor.Abstractions;
using Microsoft.Extensions.Logging;

namespace LinguaBridge.Infrastructure.External.Llm;

/// <summary>OpenAI Chat Completions (default model gpt-4o-mini). The HTTP client's base address and timeout are set at registration.</summary>
public sealed class OpenAiLlmProvider(HttpClient http, OpenAiOptions options, ILogger<OpenAiLlmProvider> logger)
    : ChatCompletionsProvider(http, logger)
{
    protected override string Name => "OpenAI";

    protected override TokenPricing Pricing => options.Pricing;

    protected override int MaxOutputTokens => options.MaxOutputTokens;

    protected override string ResolveModel(LlmRequest request) =>
        string.IsNullOrWhiteSpace(request.ModelId) ? options.Model : request.ModelId.Trim();

    protected override HttpRequestMessage CreateHttpRequest(string model)
    {
        if (!options.IsConfigured)
            throw new LlmProviderException("OpenAI is not configured: set 'OpenAI:ApiKey'.");

        var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return message;
    }
}
