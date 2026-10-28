using System.ComponentModel.DataAnnotations;
using AppInHouse.Identity.Application.Admin.Common;

namespace AppInHouse.Identity.Application.Admin.Clients;

public enum ClientApplicationType
{
    /// <summary>Server-side web app: confidential client, Authorization Code + PKCE.</summary>
    Web,

    /// <summary>Browser single-page app: public client, Authorization Code + PKCE.</summary>
    Spa,

    /// <summary>Native/mobile app: public client, Authorization Code + PKCE.</summary>
    Mobile,

    /// <summary>Service-to-service: confidential client, Client Credentials.</summary>
    Machine,
}

public sealed record ClientDto(
    string Id,
    string ClientId,
    string DisplayName,
    ClientApplicationType Type,
    bool IsConfidential,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> PostLogoutRedirectUris,
    IReadOnlyList<string> Scopes,
    bool AllowRefreshTokens);

public sealed class CreateClientRequest
{
    [Required, StringLength(100, MinimumLength = 3), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$")]
    public string ClientId { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public ClientApplicationType? Type { get; set; }

    public List<string> RedirectUris { get; set; } = new();

    public List<string> PostLogoutRedirectUris { get; set; } = new();

    /// <summary>Names of existing scopes the client may request.</summary>
    public List<string> Scopes { get; set; } = new();

    public bool AllowRefreshTokens { get; set; }
}

/// <summary>The client id and the type are immutable.</summary>
public sealed class UpdateClientRequest
{
    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    public List<string> RedirectUris { get; set; } = new();

    public List<string> PostLogoutRedirectUris { get; set; } = new();

    public List<string> Scopes { get; set; } = new();

    public bool AllowRefreshTokens { get; set; }
}

/// <param name="ClientSecret">The plain secret, returned exactly once; null for public clients.</param>
public sealed record CreatedClientDto(ClientDto Client, string? ClientSecret);

/// <param name="ClientSecret">The new plain secret, returned exactly once.</param>
public sealed record RotatedSecretDto(string ClientId, string ClientSecret);

public interface IClientAdminService
{
    Task<PagedResult<ClientDto>> ListAsync(PagingQuery query, CancellationToken cancellationToken = default);

    Task<CreatedClientDto> CreateAsync(CreateClientRequest request, CancellationToken cancellationToken = default);

    Task<ClientDto> UpdateAsync(string id, UpdateClientRequest request, CancellationToken cancellationToken = default);

    /// <summary>Generates a new secret and invalidates the previous one.</summary>
    Task<RotatedSecretDto> RotateSecretAsync(string id, CancellationToken cancellationToken = default);
}
