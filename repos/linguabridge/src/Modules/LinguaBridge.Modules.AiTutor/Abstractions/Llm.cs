namespace LinguaBridge.Modules.AiTutor.Abstractions;

/// <summary>A language model the tutor can ask for a reply. Implemented outside the module (OpenAI, Azure OpenAI).</summary>
public interface ILlmProvider
{
    /// <exception cref="LlmProviderException">The provider could not produce a reply (not configured, unreachable, rejected the call).</exception>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

/// <summary>One earlier turn of the conversation, replayed to the model so it can follow the thread.</summary>
public sealed record LlmTurn(bool FromLearner, string Text);

/// <param name="SystemPrompt">The tutor's rules. Never contains learner text.</param>
/// <param name="UserMessage">The learner's latest message, sent as a user turn so it cannot rewrite the rules.</param>
/// <param name="ModelId">Model to use; empty means the provider's configured default.</param>
/// <param name="History">Earlier turns, oldest first.</param>
public sealed record LlmRequest(string SystemPrompt, string UserMessage, string ModelId, IReadOnlyList<LlmTurn>? History = null);

/// <param name="ReplyText">The model's raw output (JSON when the model followed the prompt).</param>
/// <param name="ModelProvider">Short label recorded in the ledger, e.g. <c>OpenAI_gpt-4o-mini</c>.</param>
public sealed record LlmResponse(string ReplyText, int PromptTokens, int CompletionTokens, decimal EstimatedCostUsd, string ModelProvider = "Unknown");

/// <summary>The model could not be reached or refused the call. The message is safe to log, not to show to learners.</summary>
public class LlmProviderException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>The learner has used up today's AI allowance.</summary>
public sealed class AiQuotaExceededException(DateTimeOffset resetsAt) : Exception(DefaultMessage)
{
    public const string DefaultMessage = "AI quota exhausted. Upgrade to VIP or wait until tomorrow.";

    /// <summary>When the daily window rolls over (next UTC midnight).</summary>
    public DateTimeOffset ResetsAt { get; } = resetsAt;
}
