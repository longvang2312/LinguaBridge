using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Audit;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests.Support;

internal sealed class FakeApiKeyRepository : IApiKeyRepository
{
    public List<ApiKey> Rows { get; } = [];

    public Task<IReadOnlyList<ApiKey>> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApiKey>>([.. Rows.Where(k => k.KeyPrefix == keyPrefix)]);

    public Task AddAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        Rows.Add(key);
        return Task.CompletedTask;
    }

    public Task<bool> AnyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Rows.Count > 0);

    public Task<IReadOnlyList<ApiKey>> ListAsync(string? projectCode, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApiKey>>([.. Rows.Where(k => projectCode is null || k.ProjectCode == projectCode).OrderByDescending(k => k.CreatedAt)]);

    public Task<ApiKey?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Rows.FirstOrDefault(k => k.Id == id));
}

internal sealed class FakeAuditLogRepository : IAuditLogRepository
{
    public List<AuditLog> Rows { get; } = [];

    public (string? Actor, string? Action, DateTimeOffset? From, DateTimeOffset? To, int Limit, int Offset)? LastQuery { get; private set; }

    public Task<IReadOnlyList<AuditLog>> ListAsync(string? actor, string? action, DateTimeOffset? from, DateTimeOffset? to, int limit, int offset, CancellationToken cancellationToken = default)
    {
        LastQuery = (actor, action, from, to, limit, offset);
        return Task.FromResult<IReadOnlyList<AuditLog>>([.. Rows
            .Where(l => (actor is null || l.Actor.Contains(actor)) && (action is null || l.Action == action) && (from is null || l.CreatedAt >= from) && (to is null || l.CreatedAt < to))
            .OrderByDescending(l => l.CreatedAt).Skip(offset).Take(limit)]);
    }

    public Task<IReadOnlyList<string>> ActionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Rows.Select(l => l.Action).Distinct().Order()]);
}
