using LinguaBridge.Domain.Common;

namespace LinguaBridge.Modules.AiTutor.Entities;

/// <summary>Append-only record of one LLM/speech call, used to enforce per-user daily quotas and track cost.</summary>
public class AiUsageLedger : BaseEntity
{
    public const int TaskTypeMaxLength = 30;
    public const int ModelProviderMaxLength = 50;

    // Required by EF Core.
    private AiUsageLedger() { }

    public AiUsageLedger(
        Guid userId,
        string taskType,
        string modelProvider,
        int promptTokens,
        int completionTokens,
        decimal estimatedCostUsd)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedCostUsd);

        UserId = Guard.RequiredId(userId, nameof(userId));
        TaskType = Guard.Required(taskType, TaskTypeMaxLength, nameof(taskType));
        ModelProvider = Guard.Required(modelProvider, ModelProviderMaxLength, nameof(modelProvider));
        PromptTokens = Guard.NonNegative(promptTokens, nameof(promptTokens));
        CompletionTokens = Guard.NonNegative(completionTokens, nameof(completionTokens));
        TotalTokens = checked(promptTokens + completionTokens);
        EstimatedCostUsd = estimatedCostUsd;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }

    /// <summary>E.g. <c>TextExplanation</c>, <c>Roleplay</c>, <c>SpeechToText</c>.</summary>
    public string TaskType { get; private set; } = string.Empty;

    /// <summary>E.g. <c>OpenAI_GPT4o_Mini</c>, <c>AzureSpeech</c>.</summary>
    public string ModelProvider { get; private set; } = string.Empty;

    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public int TotalTokens { get; private set; }
    public decimal EstimatedCostUsd { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
