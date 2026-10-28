using System.Security.Cryptography;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Clients;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Security;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

/// <summary>
/// Manages OpenIddict applications. Client secrets are generated here, handed to OpenIddict (which stores only a hash) and
/// returned to the caller exactly once; they never reach the audit trail or the logs.
/// </summary>
public sealed class ClientAdminService : IClientAdminService
{
    private static readonly string[] InteractiveScopes = { Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess };

    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictScopeManager _scopes;
    private readonly IAdminAuthorizer _authorizer;
    private readonly IAuditService _audit;

    public ClientAdminService(
        IOpenIddictApplicationManager applications,
        IOpenIddictScopeManager scopes,
        IAdminAuthorizer authorizer,
        IAuditService audit)
    {
        _applications = applications;
        _scopes = scopes;
        _authorizer = authorizer;
        _audit = audit;
    }

    public async Task<PagedResult<ClientDto>> ListAsync(PagingQuery query, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();
        query.EnsureValid();

        var total = await _applications.CountAsync(cancellationToken);
        var items = new List<ClientDto>();
        await foreach (var application in _applications.ListAsync(query.PageSize, (query.Page - 1) * query.PageSize, cancellationToken))
            items.Add(await ToDtoAsync(application, cancellationToken));

        return new PagedResult<ClientDto>(items, query.Page, query.PageSize, total);
    }

    public async Task<ClientDto> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var application = await _applications.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("The client was not found.");

        return await ToDtoAsync(application, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListScopesAsync(CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var names = new List<string>();
        await foreach (var scope in _scopes.ListAsync(cancellationToken: cancellationToken))
        {
            if (await _scopes.GetNameAsync(scope, cancellationToken) is { } name)
                names.Add(name);
        }

        return names.Order(StringComparer.Ordinal).ToList();
    }

    public async Task<CreatedClientDto> CreateAsync(CreateClientRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        if (request.Type is not { } type)
            throw new BadRequestException("Type is required.");

        var clientId = request.ClientId.Trim();
        if (await _applications.FindByClientIdAsync(clientId, cancellationToken) is not null)
            throw new ConflictException($"A client with the id '{clientId}' already exists.");

        var descriptor = new OpenIddictApplicationDescriptor { ClientId = clientId };
        await ApplyAsync(descriptor, type, request.DisplayName, request.RedirectUris, request.PostLogoutRedirectUris,
            request.Scopes, request.AllowRefreshTokens, cancellationToken);

        string? secret = null;
        if (IsConfidential(type))
        {
            secret = GenerateSecret();
            descriptor.ClientSecret = secret; // OpenIddict hashes it before storing
        }

        var application = await _applications.CreateAsync(descriptor, cancellationToken);
        var dto = await ToDtoAsync(application, cancellationToken);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Create, AuditEntities.Client, dto.Id, NewValues: Snapshot(dto)),
            cancellationToken);

        return new CreatedClientDto(dto, secret);
    }

    public async Task<ClientDto> UpdateAsync(string id, UpdateClientRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var application = await _applications.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("The client was not found.");

        var before = await ToDtoAsync(application, cancellationToken);

        // Start from the stored state (hashed secret included, which OpenIddict recognises and does not hash again).
        var descriptor = new OpenIddictApplicationDescriptor();
        await _applications.PopulateAsync(descriptor, application, cancellationToken);

        await ApplyAsync(descriptor, before.Type, request.DisplayName, request.RedirectUris, request.PostLogoutRedirectUris,
            request.Scopes, request.AllowRefreshTokens, cancellationToken);

        await _applications.UpdateAsync(application, descriptor, cancellationToken);
        var after = await ToDtoAsync(application, cancellationToken);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Update, AuditEntities.Client, after.Id, OldValues: Snapshot(before), NewValues: Snapshot(after)),
            cancellationToken);

        return after;
    }

    public async Task<RotatedSecretDto> RotateSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var application = await _applications.FindByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException("The client was not found.");

        var clientId = await _applications.GetClientIdAsync(application, cancellationToken) ?? string.Empty;
        if (await _applications.GetClientTypeAsync(application, cancellationToken) != ClientTypes.Confidential)
            throw new BadRequestException("Public clients have no secret to rotate.");

        var descriptor = new OpenIddictApplicationDescriptor();
        await _applications.PopulateAsync(descriptor, application, cancellationToken);

        var secret = GenerateSecret();
        descriptor.ClientSecret = secret;

        // The previous secret stops working as soon as the new hash replaces it.
        await _applications.UpdateAsync(application, descriptor, cancellationToken);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.RotateSecret, AuditEntities.Client, id, NewValues: new { clientId, rotated = true }),
            cancellationToken);

        return new RotatedSecretDto(clientId, secret);
    }

    /// <summary>Applies the type-specific rules and the editable fields to the descriptor.</summary>
    private async Task ApplyAsync(
        OpenIddictApplicationDescriptor descriptor,
        ClientApplicationType type,
        string displayName,
        List<string> redirectUris,
        List<string> postLogoutRedirectUris,
        List<string> scopes,
        bool allowRefreshTokens,
        CancellationToken cancellationToken)
    {
        var machine = type == ClientApplicationType.Machine;
        var redirects = ParseUris(redirectUris, nameof(redirectUris), allowCustomScheme: type == ClientApplicationType.Mobile);
        var postLogouts = ParseUris(postLogoutRedirectUris, nameof(postLogoutRedirectUris), allowCustomScheme: type == ClientApplicationType.Mobile);
        var scopeNames = scopes.Select(s => s?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().Distinct().ToList();

        if (machine)
        {
            if (redirects.Count > 0 || postLogouts.Count > 0)
                throw new BadRequestException("Machine clients do not use redirect URIs.");
            if (allowRefreshTokens)
                throw new BadRequestException("Machine clients cannot use refresh tokens.");

            var interactive = scopeNames.Where(s => InteractiveScopes.Contains(s)).ToList();
            if (interactive.Count > 0)
                throw new BadRequestException($"Machine clients cannot request the scope(s): {string.Join(", ", interactive)}.");
        }
        else if (redirects.Count == 0)
        {
            throw new BadRequestException("At least one redirect URI is required.");
        }

        var unknown = new List<string>();
        foreach (var scope in scopeNames)
        {
            if (await _scopes.FindByNameAsync(scope, cancellationToken) is null)
                unknown.Add(scope);
        }

        if (unknown.Count > 0)
            throw new BadRequestException($"Unknown scope(s): {string.Join(", ", unknown)}.");

        descriptor.DisplayName = displayName.Trim();
        descriptor.ClientType = IsConfidential(type) ? ClientTypes.Confidential : ClientTypes.Public;
        descriptor.ApplicationType = type == ClientApplicationType.Mobile ? ApplicationTypes.Native : ApplicationTypes.Web;
        descriptor.ConsentType = ConsentTypes.Implicit;

        descriptor.RedirectUris.Clear();
        descriptor.PostLogoutRedirectUris.Clear();
        descriptor.Permissions.Clear();
        descriptor.Requirements.Clear();

        foreach (var uri in redirects)
            descriptor.RedirectUris.Add(uri);
        foreach (var uri in postLogouts)
            descriptor.PostLogoutRedirectUris.Add(uri);

        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        if (machine)
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        }
        else
        {
            descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
            descriptor.Permissions.Add(Permissions.Endpoints.Logout);
            descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
            descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
            descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

            scopeNames.Add(Scopes.OpenId);
            if (allowRefreshTokens)
            {
                descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
                scopeNames.Add(Scopes.OfflineAccess);
            }
        }

        foreach (var scope in scopeNames.Distinct())
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
    }

    private static List<Uri> ParseUris(List<string> values, string field, bool allowCustomScheme)
    {
        var result = new List<Uri>();
        foreach (var value in values)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment) || uri.OriginalString.Contains('*'))
                throw new BadRequestException($"'{value}' in {field} is not a valid absolute URI without wildcards or fragment.");

            var isLoopback = uri.IsLoopback;
            var ok = uri.Scheme switch
            {
                "https" => true,
                "http" => isLoopback,
                // Native apps use private-use schemes (e.g. com.example.app:/callback), never dangerous built-in ones.
                _ => allowCustomScheme && uri.Scheme is not ("javascript" or "data" or "file" or "ftp" or "vbscript" or "blob"),
            };
            if (!ok)
                throw new BadRequestException($"'{value}' in {field} must use https (http is allowed for localhost only).");

            if (!result.Contains(uri))
                result.Add(uri);
        }

        return result;
    }

    private static bool IsConfidential(ClientApplicationType type) => type is ClientApplicationType.Web or ClientApplicationType.Machine;

    private static string GenerateSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<ClientDto> ToDtoAsync(object application, CancellationToken cancellationToken)
    {
        var permissions = await _applications.GetPermissionsAsync(application, cancellationToken);
        var confidential = await _applications.GetClientTypeAsync(application, cancellationToken) == ClientTypes.Confidential;
        var native = await _applications.GetApplicationTypeAsync(application, cancellationToken) == ApplicationTypes.Native;

        var type = permissions.Contains(Permissions.GrantTypes.ClientCredentials) ? ClientApplicationType.Machine
            : confidential ? ClientApplicationType.Web
            : native ? ClientApplicationType.Mobile
            : ClientApplicationType.Spa;

        return new ClientDto(
            await _applications.GetIdAsync(application, cancellationToken) ?? string.Empty,
            await _applications.GetClientIdAsync(application, cancellationToken) ?? string.Empty,
            await _applications.GetDisplayNameAsync(application, cancellationToken) ?? string.Empty,
            type,
            confidential,
            (await _applications.GetRedirectUrisAsync(application, cancellationToken)).Order().ToList(),
            (await _applications.GetPostLogoutRedirectUrisAsync(application, cancellationToken)).Order().ToList(),
            permissions.Where(p => p.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
                .Select(p => p[Permissions.Prefixes.Scope.Length..]).Order().ToList(),
            permissions.Contains(Permissions.GrantTypes.RefreshToken));
    }

    /// <summary>Audit snapshot: descriptive fields only, never a secret.</summary>
    private static object Snapshot(ClientDto c) => new
    {
        c.Id,
        c.ClientId,
        c.DisplayName,
        type = c.Type.ToString(),
        c.IsConfidential,
        c.RedirectUris,
        c.PostLogoutRedirectUris,
        c.Scopes,
        c.AllowRefreshTokens,
    };
}
