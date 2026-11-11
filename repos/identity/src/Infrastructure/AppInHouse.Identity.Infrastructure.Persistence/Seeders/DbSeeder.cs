using AppInHouse.Identity.Application.Authorization;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AppInHouse.Identity.Infrastructure.Persistence.Seeders;

/// <summary>
/// Idempotent initial data: the <c>GlobalAdmin</c> role and default admin user, the OpenIddict scopes and the
/// default clients. Running it repeatedly never duplicates or overwrites existing data.
/// </summary>
public class DbSeeder
{
    public const string GlobalAdminRole = AuthorizationConstants.GlobalAdminRole;
    public const string AdminApiScope = "identity_admin_api";
    public const string AdminApiResourceClientId = AdminApiScope;
    public const string LinguaBridgeApiScope = "linguabridge_api";
    public const string LinguaBridgeApiResourceClientId = LinguaBridgeApiScope;
    public const string AdminPortalClientId = "admin-portal";
    public const string InternalServiceClientId = "internal-service";
    public const string LinguaBridgeAppClientId = "linguabridge-app";
    public const string SharedServicesPortalClientId = "shared-services-portal";

    private static readonly string[] DefaultLinguaBridgeAppPostLogoutRedirectUris =
    [
        "http://localhost:3000/login.html",
        "http://localhost:5173/login.html",
        "http://localhost:3000/callback",
    ];

    private static readonly string[] DefaultLinguaBridgeAppRedirectUris =
    [
        "http://localhost:3000/callback",
        "http://localhost:5173/callback",
    ];

    private static readonly string[] DefaultSharedServicesPortalRedirectUris = ["http://localhost:5031/signin-oidc"];

    private static readonly string[] DefaultSharedServicesPortalPostLogoutRedirectUris = ["http://localhost:5031/signout-callback-oidc"];

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly DbSeederOptions _options;
    private readonly ILogger<DbSeeder> _logger;

    public DbSeeder(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        IOptions<DbSeederOptions> options,
        ILogger<DbSeeder> logger)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _applicationManager = applicationManager;
        _scopeManager = scopeManager;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        ValidateOptions();

        await SeedAdminAsync();
        await SeedScopesAsync(cancellationToken);
        await SeedClientsAsync(cancellationToken);
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.AdminEmail))
            throw new InvalidOperationException($"{DbSeederOptions.SectionName}:AdminEmail is required.");
        if (string.IsNullOrWhiteSpace(_options.AdminPassword))
            throw new InvalidOperationException($"{DbSeederOptions.SectionName}:AdminPassword is required.");
        if (string.IsNullOrWhiteSpace(_options.InternalServiceSecret))
            throw new InvalidOperationException($"{DbSeederOptions.SectionName}:InternalServiceSecret is required.");
    }

    private async Task SeedAdminAsync()
    {
        if (!await _roleManager.RoleExistsAsync(GlobalAdminRole))
        {
            var roleResult = await _roleManager.CreateAsync(new ApplicationRole(GlobalAdminRole)
            {
                Description = "Full access to every project and to the identity platform."
            });
            EnsureSucceeded(roleResult, $"create role '{GlobalAdminRole}'");
            _logger.LogInformation("Seeded role {Role}.", GlobalAdminRole);
        }

        var user = await _userManager.FindByEmailAsync(_options.AdminEmail);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = _options.AdminEmail,
                Email = _options.AdminEmail,
                EmailConfirmed = true,
                FullName = "System Administrator",
                IsActive = true,
            };

            var createResult = await _userManager.CreateAsync(user, _options.AdminPassword);
            EnsureSucceeded(createResult, $"create user '{_options.AdminEmail}'");
            _logger.LogInformation("Seeded admin user {Email}.", _options.AdminEmail);
        }

        if (!await _userManager.IsInRoleAsync(user, GlobalAdminRole))
        {
            var roleResult = await _userManager.AddToRoleAsync(user, GlobalAdminRole);
            EnsureSucceeded(roleResult, $"add '{_options.AdminEmail}' to role '{GlobalAdminRole}'");
        }
    }

    private async Task SeedScopesAsync(CancellationToken cancellationToken)
    {
        var scopes = new List<OpenIddictScopeDescriptor>
        {
            new OpenIddictScopeDescriptor { Name = Scopes.OpenId, DisplayName = "OpenID" },
            new OpenIddictScopeDescriptor { Name = Scopes.Profile, DisplayName = "User profile" },
            new OpenIddictScopeDescriptor { Name = Scopes.Email, DisplayName = "Email address" },
            new OpenIddictScopeDescriptor { Name = Scopes.OfflineAccess, DisplayName = "Offline access" },
            // Puts the user's roles into the ID token, which is how a portal (Shared Services) learns whether to admit them.
            new OpenIddictScopeDescriptor { Name = Scopes.Roles, DisplayName = "User roles" },
            new OpenIddictScopeDescriptor
            {
                Name = AdminApiScope,
                DisplayName = "Identity Admin API",
                Resources = { AdminApiScope },
            },
        };

        // Like its resource-server client, only seeded when the LinguaBridge API is configured to introspect.
        if (!string.IsNullOrWhiteSpace(_options.LinguaBridgeApiClientSecret))
        {
            scopes.Add(new OpenIddictScopeDescriptor
            {
                Name = LinguaBridgeApiScope,
                DisplayName = "LinguaBridge API",
                Resources = { LinguaBridgeApiScope },
            });
        }

        foreach (var descriptor in scopes)
        {
            if (await _scopeManager.FindByNameAsync(descriptor.Name!, cancellationToken) is not null)
                continue;

            await _scopeManager.CreateAsync(descriptor, cancellationToken);
            _logger.LogInformation("Seeded scope {Scope}.", descriptor.Name);
        }
    }

    private async Task SeedClientsAsync(CancellationToken cancellationToken)
    {
        await CreateClientIfMissingAsync(BuildAdminPortal(), cancellationToken);
        await CreateClientIfMissingAsync(BuildInternalService(), cancellationToken);
        await CreateClientIfMissingAsync(BuildSharedServicesPortal(), cancellationToken);
        await EnsureSharedServicesPortalHasAddressesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(_options.AdminApiClientSecret))
            await CreateClientIfMissingAsync(BuildAdminApiResourceServer(), cancellationToken);

        if (!string.IsNullOrWhiteSpace(_options.LinguaBridgeApiClientSecret))
        {
            await CreateClientIfMissingAsync(BuildLinguaBridgeApiResourceServer(), cancellationToken);

            if (_options.LinguaBridgeAppEnabled)
            {
                await CreateClientIfMissingAsync(BuildLinguaBridgeApp(), cancellationToken);
                await EnsureLinguaBridgeAppHasRedirectsAsync(cancellationToken);
                await EnsureLinguaBridgeAppHasPostLogoutRedirectsAsync(cancellationToken);
            }

            await EnsureInternalServiceMayUseLinguaBridgeAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A database seeded before the LinguaBridge API existed already has <c>internal-service</c>, and creating clients only
    /// when missing would leave it without the new scope for good. This adds that one permission to the existing client and
    /// nothing else: its secret, its other permissions and anything an administrator edited are left as they are. (If an
    /// administrator removes this permission, the next start grants it again.)
    /// </summary>
    private async Task EnsureInternalServiceMayUseLinguaBridgeAsync(CancellationToken cancellationToken)
    {
        var application = await _applicationManager.FindByClientIdAsync(InternalServiceClientId, cancellationToken);
        if (application is null)
            return;

        var permission = Permissions.Prefixes.Scope + LinguaBridgeApiScope;
        if (await _applicationManager.HasPermissionAsync(application, permission, cancellationToken))
            return;

        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        descriptor.Permissions.Add(permission);
        await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);

        _logger.LogInformation("Granted client {ClientId} the scope {Scope}.", InternalServiceClientId, LinguaBridgeApiScope);
    }

    private IEnumerable<string> LinguaBridgeAppRedirectUris() =>
        _options.LinguaBridgeAppRedirectUris.Count > 0 ? _options.LinguaBridgeAppRedirectUris : DefaultLinguaBridgeAppRedirectUris;

    /// <summary>
    /// Sign-in addresses are only written when the client is created, so adding another front end (for example the web
    /// client on a server) to an existing database would otherwise need a manual edit. This adds the configured addresses
    /// that are missing, and nothing else: the ones an administrator added stay as they are, and nothing is removed.
    /// </summary>
    private async Task EnsureLinguaBridgeAppHasRedirectsAsync(CancellationToken cancellationToken)
    {
        var application = await _applicationManager.FindByClientIdAsync(LinguaBridgeAppClientId, cancellationToken);
        if (application is null)
            return;

        var existing = await _applicationManager.GetRedirectUrisAsync(application, cancellationToken);
        var missing = LinguaBridgeAppRedirectUris()
            .Where(uri => !existing.Contains(uri, StringComparer.Ordinal))
            .ToList();
        if (missing.Count == 0)
            return;

        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        foreach (var uri in missing)
            descriptor.RedirectUris.Add(new Uri(uri));
        await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);

        _logger.LogInformation("Added {Count} redirect address(es) to client {ClientId}.", missing.Count, LinguaBridgeAppClientId);
    }

    private IEnumerable<string> LinguaBridgeAppPostLogoutRedirectUris() =>
        _options.LinguaBridgeAppPostLogoutRedirectUris.Count > 0 ? _options.LinguaBridgeAppPostLogoutRedirectUris : DefaultLinguaBridgeAppPostLogoutRedirectUris;

    /// <summary>
    /// A <c>linguabridge-app</c> seeded before logout addresses existed has none, and without one the STS cannot send the
    /// browser back after a logout. This adds the configured addresses that are missing, and nothing else: the ones an
    /// administrator added or removed stay as they are, except that a configured one that was removed comes back.
    /// </summary>
    private async Task EnsureLinguaBridgeAppHasPostLogoutRedirectsAsync(CancellationToken cancellationToken)
    {
        var application = await _applicationManager.FindByClientIdAsync(LinguaBridgeAppClientId, cancellationToken);
        if (application is null)
            return;

        var existing = await _applicationManager.GetPostLogoutRedirectUrisAsync(application, cancellationToken);
        var missing = LinguaBridgeAppPostLogoutRedirectUris()
            .Where(uri => !existing.Contains(uri, StringComparer.Ordinal))
            .ToList();
        if (missing.Count == 0)
            return;

        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        foreach (var uri in missing)
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
        await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);

        _logger.LogInformation("Added {Count} post-logout redirect address(es) to client {ClientId}.", missing.Count, LinguaBridgeAppClientId);
    }

    private async Task CreateClientIfMissingAsync(OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (await _applicationManager.FindByClientIdAsync(descriptor.ClientId!, cancellationToken) is not null)
            return;

        await _applicationManager.CreateAsync(descriptor, cancellationToken);
        _logger.LogInformation("Seeded client {ClientId}.", descriptor.ClientId);
    }

    /// <summary>Public client using Authorization Code + PKCE (and refresh tokens).</summary>
    private OpenIddictApplicationDescriptor BuildAdminPortal()
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = AdminPortalClientId,
            DisplayName = "Admin Portal",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Logout,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Email,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + AdminApiScope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        foreach (var uri in _options.AdminPortalRedirectUris)
            descriptor.RedirectUris.Add(new Uri(uri));
        foreach (var uri in _options.AdminPortalPostLogoutRedirectUris)
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));

        return descriptor;
    }

    private IEnumerable<string> SharedServicesPortalRedirectUris() =>
        _options.SharedServicesPortalRedirectUris.Count > 0 ? _options.SharedServicesPortalRedirectUris : DefaultSharedServicesPortalRedirectUris;

    private IEnumerable<string> SharedServicesPortalPostLogoutRedirectUris() =>
        _options.SharedServicesPortalPostLogoutRedirectUris.Count > 0 ? _options.SharedServicesPortalPostLogoutRedirectUris : DefaultSharedServicesPortalPostLogoutRedirectUris;

    /// <summary>
    /// The Shared Services management portal: a server-side web app using Authorization Code + PKCE. Public unless a secret is
    /// configured, in which case it is a confidential client (the portal then sends that secret at the token endpoint).
    /// </summary>
    private OpenIddictApplicationDescriptor BuildSharedServicesPortal()
    {
        var confidential = !string.IsNullOrWhiteSpace(_options.SharedServicesPortalSecret);
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = SharedServicesPortalClientId,
            DisplayName = "Shared Services Portal",
            ClientType = confidential ? ClientTypes.Confidential : ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Logout,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Email,
                Permissions.Scopes.Roles,
                Permissions.Prefixes.Scope + Scopes.OpenId,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        if (confidential)
            descriptor.ClientSecret = _options.SharedServicesPortalSecret;

        foreach (var uri in SharedServicesPortalRedirectUris())
            descriptor.RedirectUris.Add(new Uri(uri));
        foreach (var uri in SharedServicesPortalPostLogoutRedirectUris())
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));

        return descriptor;
    }

    /// <summary>
    /// Addresses are only written when the client is created, so moving the portal to a server would need a manual edit. This
    /// adds the configured addresses that are missing, and nothing else: nothing is removed and nothing an administrator edited
    /// is touched.
    /// </summary>
    private async Task EnsureSharedServicesPortalHasAddressesAsync(CancellationToken cancellationToken)
    {
        var application = await _applicationManager.FindByClientIdAsync(SharedServicesPortalClientId, cancellationToken);
        if (application is null)
            return;

        var redirects = await _applicationManager.GetRedirectUrisAsync(application, cancellationToken);
        var logouts = await _applicationManager.GetPostLogoutRedirectUrisAsync(application, cancellationToken);
        var missingRedirects = SharedServicesPortalRedirectUris().Where(uri => !redirects.Contains(uri, StringComparer.Ordinal)).ToList();
        var missingLogouts = SharedServicesPortalPostLogoutRedirectUris().Where(uri => !logouts.Contains(uri, StringComparer.Ordinal)).ToList();
        if (missingRedirects.Count == 0 && missingLogouts.Count == 0)
            return;

        var descriptor = new OpenIddictApplicationDescriptor();
        await _applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        foreach (var uri in missingRedirects)
            descriptor.RedirectUris.Add(new Uri(uri));
        foreach (var uri in missingLogouts)
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));
        await _applicationManager.UpdateAsync(application, descriptor, cancellationToken);

        _logger.LogInformation("Added {Count} address(es) to client {ClientId}.", missingRedirects.Count + missingLogouts.Count, SharedServicesPortalClientId);
    }

    /// <summary>Confidential client for service-to-service calls (Client Credentials).</summary>
    private OpenIddictApplicationDescriptor BuildInternalService()
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = InternalServiceClientId,
            ClientSecret = _options.InternalServiceSecret,
            DisplayName = "Internal Service",
            ClientType = ClientTypes.Confidential,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + AdminApiScope,
            },
        };

        // Only where the LinguaBridge scope exists (it is seeded when the LinguaBridge API is configured).
        if (!string.IsNullOrWhiteSpace(_options.LinguaBridgeApiClientSecret))
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + LinguaBridgeApiScope);

        return descriptor;
    }

    /// <summary>The learners' app: a public client using Authorization Code + PKCE (and refresh tokens).</summary>
    private OpenIddictApplicationDescriptor BuildLinguaBridgeApp()
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = LinguaBridgeAppClientId,
            DisplayName = "LinguaBridge Student Client",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Logout,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Email,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + LinguaBridgeApiScope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        IEnumerable<string> redirectUris = LinguaBridgeAppRedirectUris();
        foreach (var uri in redirectUris)
            descriptor.RedirectUris.Add(new Uri(uri));
        foreach (var uri in LinguaBridgeAppPostLogoutRedirectUris())
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri));

        return descriptor;
    }

    /// <summary>
    /// The Admin API as a resource server. OpenIddict only lets a client introspect tokens issued for it (its client id
    /// must be one of the token's audiences), hence the client id equals the audience of the admin scope.
    /// </summary>
    private OpenIddictApplicationDescriptor BuildAdminApiResourceServer() => new()
    {
        ClientId = AdminApiResourceClientId,
        ClientSecret = _options.AdminApiClientSecret,
        DisplayName = "Identity Admin API",
        ClientType = ClientTypes.Confidential,
        Permissions = { Permissions.Endpoints.Introspection },
    };

    /// <summary>The LinguaBridge API as a resource server; same shape as the Admin API (client id = audience of its scope).</summary>
    private OpenIddictApplicationDescriptor BuildLinguaBridgeApiResourceServer() => new()
    {
        ClientId = LinguaBridgeApiResourceClientId,
        ClientSecret = _options.LinguaBridgeApiClientSecret,
        DisplayName = "LinguaBridge API",
        ClientType = ClientTypes.Confidential,
        Permissions = { Permissions.Endpoints.Introspection },
    };

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded)
            return;

        var errors = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
        throw new InvalidOperationException($"Seeding failed to {operation}: {errors}");
    }
}
