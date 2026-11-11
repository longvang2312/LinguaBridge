using System.Security.Cryptography;
using System.Text;

namespace AppInHouse.SharedServices.Domain;

/// <summary>What a key can do right now.</summary>
public enum ApiKeyStatus
{
    Active,
    Expired,
    Revoked,
}

public static class ApiScopes
{
    public const string ConfigRead = "config:read";
    public const string ConfigWrite = "config:write";
    public const string EmailSend = "email:send";
    public const string TelegramSend = "telegram:send";

    /// <summary>Send a chat message (Telegram, Discord, Slack). <see cref="TelegramSend"/> is accepted for it too, for callers that predate this scope.</summary>
    public const string ChatSend = "chat:send";

    /// <summary>Upload files. Also lets a key read the files of its project.</summary>
    public const string StorageUpload = "storage:upload";

    /// <summary>List, download and see the thumbnails of the files of a project (a public file needs no key).</summary>
    public const string StorageRead = "storage:read";

    /// <summary>Delete files.</summary>
    public const string StorageDelete = "storage:delete";

    /// <summary>The scopes a key can be given when it is created (the older <see cref="TelegramSend"/> is no longer offered).</summary>
    public static IReadOnlyList<string> Assignable { get; } =
        [ConfigRead, ConfigWrite, EmailSend, ChatSend, StorageUpload, StorageRead, StorageDelete];
}

/// <summary>
/// The credential a project uses to call the Shared Services API (header <c>X-Api-Key</c>). The full key
/// (<c>aih_live_</c> + 6 characters + a 256-bit random secret) exists in plain text only in the answer to the call that
/// creates it; only its SHA-256 hash and a short, non-secret prefix (used to find the row) are stored.
/// </summary>
public sealed class ApiKey
{
    public const string KeyStart = "aih_live_";

    /// <summary>"aih_live_" and 6 characters: enough to find the row, nowhere near enough to guess the key.</summary>
    public const int PrefixLength = 15;

    private const int MinKeyLength = 32;
    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    // For EF Core.
    private ApiKey()
    {
        ProjectCode = string.Empty;
        Name = string.Empty;
        KeyPrefix = string.Empty;
        HashedKey = string.Empty;
        Scopes = string.Empty;
    }

    public Guid Id { get; private set; }
    public string ProjectCode { get; private set; }
    public string Name { get; private set; }
    public string KeyPrefix { get; private set; }

    /// <summary>SHA-256 of the full key, lower-case hex (64 characters).</summary>
    public string HashedKey { get; private set; }

    /// <summary>Comma-separated, for example <c>email:send,telegram:send</c>.</summary>
    public string Scopes { get; private set; }

    public bool IsRevoked { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<string> ScopeList =>
        Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Creates a key and returns it with its plain-text form, which the caller must show once and then forget.</summary>
    public static (ApiKey Key, string PlainTextKey) Generate(
        string projectCode, string name, IEnumerable<string> scopes, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var id = new string(Enumerable.Range(0, PrefixLength - KeyStart.Length)
            .Select(_ => IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)]).ToArray());
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var plain = KeyStart + id + secret;
        return (FromPlainText(projectCode, name, scopes, plain, expiresAt, now), plain);
    }

    /// <summary>Wraps a key somebody already has (for example a development key from configuration).</summary>
    public static ApiKey FromPlainText(
        string projectCode, string name, IEnumerable<string> scopes, string plainTextKey, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (!TryGetPrefix(plainTextKey, out var prefix))
            throw new DomainException($"An API key starts with '{KeyStart}' and is at least {MinKeyLength} characters long.");
        if (string.IsNullOrWhiteSpace(projectCode)) throw new DomainException("An API key belongs to a project.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("An API key needs a name.");

        var scopeText = string.Join(',', scopes.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal));
        if (scopeText.Length == 0) throw new DomainException("An API key needs at least one scope.");

        return new ApiKey
        {
            Id = Guid.NewGuid(),
            ProjectCode = projectCode,
            Name = name,
            KeyPrefix = prefix,
            HashedKey = Hash(plainTextKey),
            Scopes = scopeText,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        };
    }

    public static string Hash(string plainTextKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainTextKey))).ToLowerInvariant();

    public static bool TryGetPrefix(string? plainTextKey, out string prefix)
    {
        if (plainTextKey is { Length: >= MinKeyLength } && plainTextKey.StartsWith(KeyStart, StringComparison.Ordinal))
        {
            prefix = plainTextKey[..PrefixLength];
            return true;
        }

        prefix = string.Empty;
        return false;
    }

    /// <summary>Compares in constant time, so the answer does not leak how many characters were right.</summary>
    public bool Matches(string plainTextKey) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(plainTextKey)), Encoding.ASCII.GetBytes(HashedKey));

    public bool IsUsable(DateTimeOffset now) => !IsRevoked && (ExpiresAt is null || ExpiresAt > now);

    public bool HasScope(string scope) => ScopeList.Contains(scope, StringComparer.Ordinal);

    public ApiKeyStatus StatusAt(DateTimeOffset now) =>
        IsRevoked ? ApiKeyStatus.Revoked : ExpiresAt is not null && ExpiresAt <= now ? ApiKeyStatus.Expired : ApiKeyStatus.Active;

    public void Revoke() => IsRevoked = true;

    /// <summary>Ends the key's life at <paramref name="at"/> if that is earlier than it already ends (used to retire an old key once a new one has taken over).</summary>
    public void ShortenExpiry(DateTimeOffset at)
    {
        if (ExpiresAt is null || at < ExpiresAt) ExpiresAt = at;
    }

    public void MarkUsed(DateTimeOffset now) => LastUsedAt = now;
}
