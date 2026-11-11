using System.Security.Claims;
using System.Text.Encodings.Web;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AppInHouse.SharedServices.Api.Auth;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";

    /// <summary>The scopes of the key (or of the bearer token), one claim each.</summary>
    public const string ScopeClaim = "scope";

    public const string ProjectClaim = "project_code";
}

/// <summary>
/// Authenticates a call by its <c>X-Api-Key</c> header: the key's prefix finds the stored row, its SHA-256 hash is compared in
/// constant time, and a revoked or expired key is refused. A refusal never says which part was wrong.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>"Last used" is recorded at most this often per key, so a busy client does not cause a write per request.</summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.Header, out var header) || header.Count != 1 || string.IsNullOrWhiteSpace(header[0]))
            return AuthenticateResult.NoResult();

        var plainKey = header[0]!.Trim();
        if (!ApiKey.TryGetPrefix(plainKey, out var prefix))
            return AuthenticateResult.Fail("The API key is not valid.");

        var services = Context.RequestServices;
        var repository = services.GetRequiredService<IApiKeyRepository>();
        var now = clock.GetUtcNow();

        var candidates = await repository.FindByPrefixAsync(prefix, Context.RequestAborted);
        var key = candidates.FirstOrDefault(k => k.Matches(plainKey));
        if (key is null || !key.IsUsable(now))
            return AuthenticateResult.Fail("The API key is not valid.");

        await TouchAsync(key, now, services);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, key.Id.ToString()),
            new(ClaimTypes.Name, $"apikey:{key.KeyPrefix}"),
            new(ApiKeyDefaults.ProjectClaim, key.ProjectCode),
        };
        claims.AddRange(key.ScopeList.Select(scope => new Claim(ApiKeyDefaults.ScopeClaim, scope)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private async Task TouchAsync(ApiKey key, DateTimeOffset now, IServiceProvider services)
    {
        if (key.LastUsedAt is { } last && now - last < LastUsedGranularity) return;

        try
        {
            key.MarkUsed(now);
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Knowing when a key was last used is a convenience; it must never make a valid call fail.
            Logger.LogWarning(ex, "Could not record the last use of API key {Prefix}.", key.KeyPrefix);
        }
    }
}
