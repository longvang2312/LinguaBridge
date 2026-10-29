using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Domain.Enums;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Entities;
using LinguaBridge.Modules.AiTutor.Prompting;

namespace LinguaBridge.Modules.AiTutor.Commands;

/// <param name="ScenarioCode">One of <see cref="TutorScenarios.All"/>, in any casing.</param>
/// <param name="Title">Optional; defaults to the scenario and language.</param>
public sealed record CreateSessionCommand(Guid UserId, TargetLanguage TargetLanguage, string ScenarioCode, string? Title = null);

public sealed record EndSessionCommand(Guid UserId, Guid SessionId);

public sealed class CreateSessionValidator : AbstractValidator<CreateSessionCommand>
{
    public CreateSessionValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.TargetLanguage).IsInEnum();
        RuleFor(c => c.ScenarioCode)
            .Must(code => TutorScenarios.TryNormalize(code, out _))
            .WithMessage($"Scenario must be one of: {string.Join(", ", TutorScenarios.All)}.");
        RuleFor(c => c.Title).MaximumLength(ChatSession.TitleMaxLength);
    }
}

public sealed class CreateSessionHandler(IAiTutorRepository repository, IValidator<CreateSessionCommand> validator)
{
    public async Task<Result<ChatSessionDto>> HandleAsync(CreateSessionCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        TutorScenarios.TryNormalize(command.ScenarioCode, out var scenario);
        var title = string.IsNullOrWhiteSpace(command.Title) ? $"{scenario} ({command.TargetLanguage.ToCode()})" : command.Title.Trim();

        var session = new ChatSession(command.UserId, command.TargetLanguage, scenario!, title);
        repository.Add(session);
        await repository.SaveChangesAsync(cancellationToken);

        return session.ToDto();
    }
}

/// <summary>Ends a session. Ending twice is not an error, so a retried request is harmless.</summary>
public sealed class EndSessionHandler(IAiTutorRepository repository)
{
    public async Task<Result> HandleAsync(EndSessionCommand command, CancellationToken cancellationToken = default)
    {
        var session = await repository.GetSessionAsync(command.SessionId, command.UserId, cancellationToken);
        if (session is null)
            return CommandSupport.SessionNotFound(command.SessionId);

        session.End();
        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

internal static class SessionMapping
{
    public static ChatSessionDto ToDto(this ChatSession session) => new(
        session.Id,
        session.TargetLanguage.ToCode(),
        session.ScenarioCode,
        session.Title,
        session.StartedAt,
        session.EndedAt,
        session.TotalMessagesCount);
}
