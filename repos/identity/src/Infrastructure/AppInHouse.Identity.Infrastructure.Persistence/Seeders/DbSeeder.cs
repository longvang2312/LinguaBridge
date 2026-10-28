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
    public const string AdminPortalClientId = "admin-portal";
    public const string InternalServiceClientId = "internal-service";

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
        var scopes = new[]
        {
            new OpenIddictScopeDescriptor { Name = Scopes.OpenId, DisplayName = "OpenID" },
            new OpenIddictScopeDescriptor { Name = Scopes.Profile, DisplayName = "User profile" },
            new OpenIddictScopeDescriptor { Name = Scopes.Email, DisplayName = "Email address" },
            new OpenIddictScopeDescriptor { Name = Scopes.OfflineAccess, DisplayName = "Offline access" },
            new OpenIddictScopeDescriptor
            {
                Name = AdminApiScope,
                DisplayName = "Identity Admin API",
                Resources = { AdminApiScope },
            },
        };

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

        if (!string.IsNullOrWhiteSpace(_options.AdminApiClientSecret))
            await CreateClientIfMissingAsync(BuildAdminApiResourceServer(), cancellationToken);
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

    /// <summary>Confidential client for service-to-service calls (Client Credentials).</summary>
    private OpenIddictApplicationDescriptor BuildInternalService() => new()
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

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (result.Succeeded)
            return;

        var errors = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
        throw new InvalidOperationException($"Seeding failed to {operation}: {errors}");
    }
}
