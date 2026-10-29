using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Commands;
using LinguaBridge.Modules.AiTutor.Quota;

namespace LinguaBridge.Modules.AiTutor.Queries;

public sealed record GetMessagesQuery(Guid UserId, Guid SessionId, int Offset = 0, int Limit = GetMessagesQuery.DefaultLimit)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

public sealed record GetQuotaQuery(Guid UserId);

/// <summary>The conversation so far, oldest first. Readable after the session has ended.</summary>
public sealed class GetMessagesHandler(IAiTutorRepository repository)
{
    public async Task<Result<MessagesPageDto>> HandleAsync(GetMessagesQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Offset < 0)
            return Error.Validation(nameof(query.Offset), "Offset must not be negative.");
        if (query.Limit is < 1 or > GetMessagesQuery.MaxLimit)
            return Error.Validation(nameof(query.Limit), $"Limit must be between 1 and {GetMessagesQuery.MaxLimit}.");

        var session = await repository.GetSessionAsync(query.SessionId, query.UserId, cancellationToken);
        if (session is null)
            return CommandSupport.SessionNotFound(query.SessionId);

        var (items, total) = await repository.GetMessagesAsync(session.Id, query.Offset, query.Limit, cancellationToken);
        return new MessagesPageDto(items.Select(m => m.ToDto()).ToList(), total, query.Offset, query.Limit);
    }
}

public sealed class GetQuotaHandler(AiQuotaPolicy quota)
{
    public async Task<AiQuotaDto> HandleAsync(GetQuotaQuery query, CancellationToken cancellationToken = default) =>
        await quota.GetStatusAsync(query.UserId, cancellationToken);
}
