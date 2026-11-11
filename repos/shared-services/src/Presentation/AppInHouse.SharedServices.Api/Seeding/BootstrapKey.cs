using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Api.Seeding;

/// <summary>
/// The first key. A new deployment has no API key, and keys can only be made with a key, so the host may be given one
/// (<c>SharedServices:Bootstrap:AdminApiKey</c>, from the environment or a secret store, never from a file in the repository). It is used
/// once: if the database holds no key at all, a platform key with every scope is made from it; afterwards the setting is ignored and
/// should be removed. Only its prefix is ever logged.
/// </summary>
public static class BootstrapKey
{
    public static async Task EnsureAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var plain = configuration["SharedServices:Bootstrap:AdminApiKey"];
        if (string.IsNullOrWhiteSpace(plain)) return;

        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("BootstrapKey");

        if (!ApiKey.TryGetPrefix(plain.Trim(), out var prefix))
            throw new InvalidOperationException($"SharedServices:Bootstrap:AdminApiKey must start with '{ApiKey.KeyStart}' and be at least 32 characters long.");

        var keys = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        if (await keys.AnyAsync(cancellationToken))
        {
            logger.LogInformation("The database already has API keys; the bootstrap key setting is ignored (remove it).");
            return;
        }

        await keys.AddAsync(ApiKey.FromPlainText(
            "default", "Bootstrap admin key", ApiScopes.Assignable, plain.Trim(), expiresAt: null, TimeProvider.System.GetUtcNow()), cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(cancellationToken);
        logger.LogWarning("Created the bootstrap admin API key ({Prefix}...). Make real keys in the portal, then remove the setting and revoke this key.", prefix);
    }
}
