using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.ApiKeys;

/// <summary>Make a key. <see cref="ExpiresInDays"/> null means it never expires; otherwise 1 to 3650 days from now.</summary>
public sealed record CreateApiKeyCommand(string ProjectCode, string Name, IReadOnlyCollection<string> Scopes, int? ExpiresInDays = null);

/// <summary>A key as a client may see it: what it is for and what state it is in, never the key itself and never its hash.</summary>
public sealed record ApiKeyDto(
    Guid Id, string ProjectCode, string Name, string KeyPrefix, IReadOnlyList<string> Scopes, ApiKeyStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// A key that was just made, with the one and only copy of its plain text. It must be shown once and then forgotten; it is not
/// kept anywhere. <c>ToString</c> leaves the text out so that a log line that prints the record cannot leak it.
/// </summary>
public sealed record CreatedApiKeyDto(ApiKeyDto Key, string PlainTextKey)
{
    public override string ToString() => $"CreatedApiKey {Key.KeyPrefix}...";
}

/// <summary>The new key of a rotation, and the old one with the end date it was given.</summary>
public sealed record RotatedApiKeyDto(CreatedApiKeyDto NewKey, ApiKeyDto OldKey);

public interface IApiKeyManager
{
    /// <exception cref="ValidationException">An unknown scope, no scope, a bad name, project or expiry.</exception>
    Task<CreatedApiKeyDto> CreateAsync(CreateApiKeyCommand command, CancellationToken cancellationToken = default);

    /// <summary>The keys, newest first. A null project means every project.</summary>
    Task<IReadOnlyList<ApiKeyDto>> ListAsync(string? projectCode, CancellationToken cancellationToken = default);

    Task<ApiKeyDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Revokes the key at once. Revoking a key that is already revoked changes nothing.</summary>
    Task<ApiKeyDto> RevokeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Key rotation with a grace period: a new key with the same project, name and scopes is made, and the old one keeps working only
    /// until <paramref name="graceDays"/> days from now, so a client can switch over without a gap. Both are valid in between.
    /// </summary>
    Task<RotatedApiKeyDto> RotateAsync(Guid id, int graceDays, CancellationToken cancellationToken = default);
}

/// <summary>
/// The life of an API key: made once (its plain text is returned only by that call; only the SHA-256 and a 15-character prefix are
/// stored), listed without anything secret, revoked at once, or rotated with a grace period. Every change is audited with the prefix,
/// never the key.
/// </summary>
public sealed class ApiKeyManager(IApiKeyRepository repository, IAuditLogWriter audit, IUnitOfWork unitOfWork, TimeProvider clock) : IApiKeyManager
{
    public const int MaxExpiryDays = 3650;
    public const int MaxGraceDays = 90;

    public async Task<CreatedApiKeyDto> CreateAsync(CreateApiKeyCommand command, CancellationToken cancellationToken = default)
    {
        var project = ChannelConfigurationValidator.NormalizeProjectCode(command.ProjectCode);
        var errors = new Dictionary<string, string[]>();

        if (!ChannelConfigurationValidator.IsValidProjectCode(project))
            errors[nameof(command.ProjectCode)] = ["Use 1 to 50 lower-case letters, digits, '-' or '_', starting with a letter or digit."];

        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100 || name.Any(char.IsControl)) errors[nameof(command.Name)] = ["The name is required, at most 100 characters, on one line."];

        var scopes = (command.Scopes ?? []).Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0) errors[nameof(command.Scopes)] = ["Choose at least one scope."];
        else if (scopes.FirstOrDefault(s => !ApiScopes.Assignable.Contains(s)) is { } unknown)
            errors[nameof(command.Scopes)] = [$"'{unknown}' is not a scope a key can have. Choose from: {string.Join(", ", ApiScopes.Assignable)}."];

        if (command.ExpiresInDays is { } days && days is < 1 or > MaxExpiryDays)
            errors[nameof(command.ExpiresInDays)] = [$"The lifetime is 1 to {MaxExpiryDays} days, or none for a key that does not expire."];

        if (errors.Count > 0) throw new ValidationException(errors);

        var now = clock.GetUtcNow();
        var (key, plain) = ApiKey.Generate(project, name, scopes, command.ExpiresInDays is { } d ? now.AddDays(d) : null, now);

        await repository.AddAsync(key, cancellationToken);
        audit.Record("CreateApiKey", "ApiKey", key.Id.ToString(), new
        {
            key.ProjectCode, key.Name, key.KeyPrefix, Scopes = key.ScopeList, key.ExpiresAt,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreatedApiKeyDto(ToDto(key, now), plain);
    }

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(string? projectCode, CancellationToken cancellationToken = default)
    {
        var project = string.IsNullOrWhiteSpace(projectCode) ? null : ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        var now = clock.GetUtcNow();
        return [.. (await repository.ListAsync(project, cancellationToken)).Select(k => ToDto(k, now))];
    }

    public async Task<ApiKeyDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var key = await repository.GetByIdAsync(id, cancellationToken);
        return key is null ? null : ToDto(key, clock.GetUtcNow());
    }

    public async Task<ApiKeyDto> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var key = await repository.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException($"API key {id} does not exist.");

        if (!key.IsRevoked)
        {
            key.Revoke();
            audit.Record("RevokeApiKey", "ApiKey", key.Id.ToString(), new { key.ProjectCode, key.Name, key.KeyPrefix });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return ToDto(key, clock.GetUtcNow());
    }

    public async Task<RotatedApiKeyDto> RotateAsync(Guid id, int graceDays, CancellationToken cancellationToken = default)
    {
        if (graceDays is < 0 or > MaxGraceDays)
            throw new ValidationException(new Dictionary<string, string[]> { ["GraceDays"] = [$"The grace period is 0 to {MaxGraceDays} days."] });

        var old = await repository.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException($"API key {id} does not exist.");
        var now = clock.GetUtcNow();
        if (old.StatusAt(now) != ApiKeyStatus.Active)
            throw new ValidationException(new Dictionary<string, string[]> { ["Id"] = ["Only a key that is still valid can be rotated."] });

        var (replacement, plain) = ApiKey.Generate(old.ProjectCode, old.Name, old.ScopeList, old.ExpiresAt, now);
        await repository.AddAsync(replacement, cancellationToken);

        // Grace period zero means "retire the old key now": its end date is the moment of rotation, which IsUsable already treats as over.
        old.ShortenExpiry(now.AddDays(graceDays));

        audit.Record("RotateApiKey", "ApiKey", old.Id.ToString(), new
        {
            old.ProjectCode, old.Name, OldKeyPrefix = old.KeyPrefix, NewKeyPrefix = replacement.KeyPrefix, GraceDays = graceDays, OldKeyEndsAt = old.ExpiresAt,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new RotatedApiKeyDto(new CreatedApiKeyDto(ToDto(replacement, now), plain), ToDto(old, now));
    }

    private static ApiKeyDto ToDto(ApiKey key, DateTimeOffset now) =>
        new(key.Id, key.ProjectCode, key.Name, key.KeyPrefix, key.ScopeList, key.StatusAt(now), key.CreatedAt, key.ExpiresAt, key.LastUsedAt);
}
