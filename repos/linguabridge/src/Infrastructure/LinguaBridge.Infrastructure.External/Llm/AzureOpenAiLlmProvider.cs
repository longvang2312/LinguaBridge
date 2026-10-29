using LinguaBridge.Modules.AiTutor.Abstractions;
using Microsoft.Extensions.Logging;

namespace LinguaBridge.Infrastructure.External.Llm;

/// <summary>
/// Azure OpenAI Chat Completions. The model is the configured deployment, so the request's model id is ignored:
/// asking Azure for a model that was not deployed would only fail.
/// </summary>
public sealed class AzureOpenAiLlmProvider(HttpClient http, AzureOpenAiOptions options, ILogger<AzureOpenAiLlmProvider> logger)
    : ChatCompletionsProvider(http, logger)
{
    protected override string Name => "AzureOpenAI";

    protected override TokenPricing Pricing => options.Pricing;

    protected override int MaxOutputTokens => options.MaxOutputTokens;

    protected override bool BodyNamesModel => false;

    protected override string ResolveModel(LlmRequest request) => options.Deployment;

    protected override HttpRequestMessage CreateHttpRequest(string model)
    {
        if (!options.IsConfigured)
            throw new LlmProviderException("Azure OpenAI is not configured: set 'AzureOpenAI:Endpoint', 'ApiKey' and 'Deployment'.");

        var uri = $"{options.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(model)}/chat/completions?api-version={Uri.EscapeDataString(options.ApiVersion)}";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var address))
            throw new LlmProviderException("'AzureOpenAI:Endpoint' is not a valid URL.");

        var message = new HttpRequestMessage(HttpMethod.Post, address);
        message.Headers.Add("api-key", options.ApiKey);
        return message;
    }
}
