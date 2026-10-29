using System.Net.Http.Json;
using System.Text.Json;
using LinguaBridge.Modules.AiTutor.Abstractions;
using Microsoft.Extensions.Logging;

namespace LinguaBridge.Infrastructure.External.Llm;

/// <summary>
/// The chat-completions wire format shared by OpenAI and Azure OpenAI. Subclasses supply where to call, how to
/// authenticate and what a token costs. Every failure to get a reply becomes <see cref="LlmProviderException"/>,
/// so callers handle one exception type whatever the cause.
/// </summary>
public abstract class ChatCompletionsProvider(HttpClient http, ILogger logger) : ILlmProvider
{
    private const int LoggedBodyLength = 300;

    protected abstract string Name { get; }

    protected abstract TokenPricing Pricing { get; }

    protected abstract int MaxOutputTokens { get; }

    /// <summary>The model (or Azure deployment) to use for this request.</summary>
    protected abstract string ResolveModel(LlmRequest request);

    /// <summary>Whether the body must name the model; Azure takes it from the URL instead.</summary>
    protected virtual bool BodyNamesModel => true;

    /// <summary>The request to POST, with address and credentials. Throws <see cref="LlmProviderException"/> when not configured.</summary>
    protected abstract HttpRequestMessage CreateHttpRequest(string model);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = ResolveModel(request);
        using var message = CreateHttpRequest(model);
        message.Content = JsonContent.Create(BuildBody(request, model));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmProviderException($"{Name} could not be reached: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmProviderException($"{Name} did not answer in time.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Provider} returned {StatusCode}: {Body}", Name, (int)response.StatusCode, Shorten(body));
                throw new LlmProviderException($"{Name} returned HTTP {(int)response.StatusCode}.");
            }

            return Parse(body, request, model);
        }
    }

    private Dictionary<string, object?> BuildBody(LlmRequest request, string model)
    {
        var messages = new List<object> { new { role = "system", content = request.SystemPrompt } };
        foreach (var turn in request.History ?? [])
            messages.Add(new { role = turn.FromLearner ? "user" : "assistant", content = turn.Text });
        messages.Add(new { role = "user", content = request.UserMessage });

        var body = new Dictionary<string, object?>
        {
            ["messages"] = messages,
            ["response_format"] = new { type = "json_object" },
            ["max_tokens"] = MaxOutputTokens,
            ["temperature"] = 0.7,
        };
        if (BodyNamesModel)
            body["model"] = model;
        return body;
    }

    private LlmResponse Parse(string body, LlmRequest request, string model)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var content = root.GetProperty("choices")[0].GetProperty("message").TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(content))
                throw new LlmProviderException($"{Name} returned no reply text (the answer may have been filtered).");

            var promptTokens = TokenCount(root, "prompt_tokens");
            var completionTokens = TokenCount(root, "completion_tokens");
            if (promptTokens is null || completionTokens is null)
            {
                // Without usage the call would be free for quota purposes, so fall back to the same rough estimate.
                logger.LogWarning("{Provider} returned no token usage; estimating from text length.", Name);
                promptTokens ??= (request.SystemPrompt.Length + request.UserMessage.Length + 3) / 4;
                completionTokens ??= (content.Length + 3) / 4;
            }

            return new LlmResponse(content, promptTokens.Value, completionTokens.Value, Pricing.Cost(promptTokens.Value, completionTokens.Value), $"{Name}_{model}");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new LlmProviderException($"{Name} returned a response in an unexpected format.", ex);
        }
    }

    private static int? TokenCount(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var usage) && usage.TryGetProperty(name, out var count) && count.TryGetInt32(out var value) && value >= 0
            ? value
            : null;

    private static string Shorten(string body) =>
        body.Length <= LoggedBodyLength ? body : string.Concat(body.AsSpan(0, LoggedBodyLength), "...");
}
