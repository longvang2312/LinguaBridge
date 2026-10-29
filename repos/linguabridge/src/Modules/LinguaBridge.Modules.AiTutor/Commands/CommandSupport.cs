using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Entities;

namespace LinguaBridge.Modules.AiTutor.Commands;

internal static class CommandSupport
{
    /// <summary>Runs the validator; returns the failure to hand back, or null when the command is valid.</summary>
    public static async Task<Error?> CheckAsync<T>(this IValidator<T> validator, T command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (validation.IsValid)
            return null;

        var details = validation.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        return Error.Validation(details);
    }

    public static Error SessionNotFound(Guid id) => Error.NotFound("session.not_found", $"Chat session {id} was not found.");

    public static ChatMessageDto ToDto(this ChatMessage message) => new(
        message.Id,
        message.SenderType.ToString(),
        message.Content,
        message.PinyinOrIpa,
        message.Translation,
        message.GrammarCorrection,
        message.CreatedAt);
}
