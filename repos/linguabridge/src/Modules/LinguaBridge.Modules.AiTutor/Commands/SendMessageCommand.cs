using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Entities;
using LinguaBridge.Modules.AiTutor.Prompting;
using LinguaBridge.Modules.AiTutor.Quota;
using LinguaBridge.Modules.AiTutor.Responses;
using Microsoft.Extensions.Logging;

namespace LinguaBridge.Modules.AiTutor.Commands;

/// <param name="BaseLanguage">Language of the translation and grammar tips the tutor writes.</param>
public sealed record SendMessageCommand(Guid UserId, Guid SessionId, string Text, BaseLanguage BaseLanguage = BaseLanguage.Vietnamese);

public sealed class SendMessageValidator : AbstractValidator<SendMessageCommand>
{
    public SendMessageValidator(AiTutorOptions options)
    {
        RuleFor(c => c.Text)
            .NotEmpty().WithMessage("The message must not be empty.")
            .Must(text => text is null || text.Trim().Length <= options.MaxMessageLength)
            .WithMessage($"The message must be at most {options.MaxMessageLength} characters.");
        RuleFor(c => c.BaseLanguage).IsInEnum();
    }
}

/// <summary>
/// One tutoring turn. Order matters:
/// <list type="number">
/// <item>The allowance is claimed (a ledger row holding the estimate) before the model is called, in one atomic step, so
/// requests arriving together cannot all pass the check and each spend real money.</item>
/// <item>If the model fails, or the caller walks away mid-call, the claim is removed: no reply, no charge.</item>
/// <item>Once the model has answered, the claim is settled with the real tokens and saved together with both messages.
/// That save ignores the caller's cancellation token: the provider has already been paid, so a dropped connection
/// must not leave the call off the ledger.</item>
/// </list>
/// </summary>
public sealed class SendMessageHandler(
    IAiTutorRepository repository,
    ILearnerLevelSource levels,
    ILlmProvider llm,
    AiQuotaPolicy quota,
    AiTutorOptions options,
    ILogger<SendMessageHandler> logger,
    IValidator<SendMessageCommand> validator)
{
    private const string TaskType = "TextTutor";

    /// <exception cref="AiQuotaExceededException">Today's allowance is used up.</exception>
    /// <exception cref="LlmProviderException">The model could not produce a usable reply.</exception>
    public async Task<Result<TutorReplyDto>> HandleAsync(SendMessageCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        var session = await repository.GetSessionAsync(command.SessionId, command.UserId, cancellationToken);
        if (session is null)
            return CommandSupport.SessionNotFound(command.SessionId);
        if (session.IsEnded)
            return Error.Conflict("session.ended", "This chat session has ended. Start a new session to keep practising.");

        var text = command.Text.Trim();
        var level = await levels.GetLevelAsync(command.UserId, session.TargetLanguage, cancellationToken);
        var systemPrompt = AiTutorPromptBuilder.Build(session.TargetLanguage, level, session.ScenarioCode, command.BaseLanguage);

        var history = (await repository.GetRecentMessagesAsync(session.Id, options.HistoryMessages, cancellationToken))
            .Select(m => new LlmTurn(m.SenderType == SenderType.User, m.Content))
            .ToList();

        var estimated = AiQuotaPolicy.EstimateTokens([systemPrompt, text, .. history.Select(t => t.Text)]);
        var ledger = await quota.ReserveAsync(command.UserId, estimated, TaskType, cancellationToken);

        LlmResponse completion;
        AiTutorResponse reply;
        try
        {
            completion = await llm.CompleteAsync(new LlmRequest(systemPrompt, text, options.ModelId, history), cancellationToken);

            reply = AiTutorResponseParser.Parse(completion.ReplyText);
            if (string.IsNullOrWhiteSpace(reply.ReplyText))
                throw new LlmProviderException("The model returned an empty reply.");
        }
        catch
        {
            await ReleaseAsync(ledger);
            throw;
        }

        if (!reply.IsStructured)
            logger.LogWarning("The model did not return the requested JSON for session {SessionId}; its plain text was used as the reply.", session.Id);

        ledger.Settle(
            Truncate(completion.ModelProvider, AiUsageLedger.ModelProviderMaxLength),
            completion.PromptTokens,
            completion.CompletionTokens,
            completion.EstimatedCostUsd);
        var userMessage = session.AddMessage(SenderType.User, text);
        var assistantMessage = session.AddMessage(SenderType.Assistant, reply.ReplyText, reply.PinyinOrIpa, reply.Translation, reply.GrammarTips);

        repository.Add(userMessage);
        repository.Add(assistantMessage);
        await repository.SaveChangesAsync(CancellationToken.None);

        return new TutorReplyDto(
            assistantMessage.Id,
            reply.ReplyText,
            reply.PinyinOrIpa,
            reply.Translation,
            reply.GrammarTips,
            ledger.TotalTokens,
            reply.IsStructured,
            assistantMessage.CreatedAt);
    }

    /// <summary>Gives the claimed allowance back. A failure here is logged and must not hide the error that got us here.</summary>
    private async Task ReleaseAsync(AiUsageLedger reservation)
    {
        try
        {
            repository.Remove(reservation);
            await repository.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not release the AI quota reservation {ReservationId}; it stays counted until the day ends.", reservation.Id);
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
