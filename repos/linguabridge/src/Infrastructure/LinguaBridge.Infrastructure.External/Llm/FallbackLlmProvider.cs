using LinguaBridge.Modules.AiTutor.Abstractions;
using Microsoft.Extensions.Logging;

namespace LinguaBridge.Infrastructure.External.Llm;

/// <summary>
/// Asks the primary provider and, only if it fails, the secondary. Quota and caller cancellation are not provider
/// failures and pass through untouched.
/// </summary>
public sealed class FallbackLlmProvider(ILlmProvider primary, ILlmProvider secondary, ILogger<FallbackLlmProvider> logger) : ILlmProvider
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        try
        {
            return await primary.CompleteAsync(request, ct);
        }
        catch (LlmProviderException ex)
        {
            logger.LogWarning(ex, "The primary LLM provider failed; trying the fallback provider.");
            return await secondary.CompleteAsync(request, ct);
        }
    }
}
