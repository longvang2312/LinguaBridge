using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;

namespace AppInHouse.SharedServices.Api.Auth;

public static class Policies
{
    public const string ConfigRead = ApiScopes.ConfigRead;
    public const string ConfigWrite = ApiScopes.ConfigWrite;
    public const string EmailSend = ApiScopes.EmailSend;
    public const string ChatSend = ApiScopes.ChatSend;
    public const string StorageUpload = ApiScopes.StorageUpload;
    public const string StorageRead = ApiScopes.StorageRead;
    public const string StorageDelete = ApiScopes.StorageDelete;

    /// <summary>A notification goes out by email, chat or both, so any of the sending scopes is enough to ask for one.</summary>
    public const string NotificationSend = "notification:send";
}

/// <summary>Needs one of the scopes in the caller's claims (an API key's scopes, or the scopes of a bearer token).</summary>
public sealed class ScopeRequirement(params string[] scopes) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Scopes { get; } = scopes;
}

public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    /// <summary>A bearer token with this scope may use every endpoint (an administrator of the platform).</summary>
    public const string AdminScope = "shared_services_admin";

    // OpenIddict keeps the scopes of an access token in "oi_scp"; an API key's scopes are plain "scope" claims.
    private static readonly string[] ScopeClaimTypes = [ApiKeyDefaults.ScopeClaim, "oi_scp"];

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        if (HasAny(context.User, requirement.Scopes))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }

    /// <summary>Whether the caller has one of these scopes (or the administrator scope). Also used by endpoints that decide per resource, such as a public file.</summary>
    public static bool HasAny(System.Security.Claims.ClaimsPrincipal user, IEnumerable<string> wanted)
    {
        var scopes = user.Claims
            .Where(c => ScopeClaimTypes.Contains(c.Type))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

        return wanted.Any(scopes.Contains) || scopes.Contains(AdminScope);
    }
}

public static class AuthenticationExtensions
{
    public const string SectionName = "Authentication";
    private const string CombinedScheme = "ApiKeyOrBearer";

    /// <summary>
    /// Two ways to call the API: an API key (header <c>X-Api-Key</c>, for other projects) and, when <c>Authentication:Authority</c>
    /// names the Identity STS, a bearer token checked by asking the STS (introspection, as the other APIs do). Every endpoint
    /// needs one of them unless it opts out with <c>AllowAnonymous</c>, and the configuration endpoints also need a scope.
    /// </summary>
    public static IServiceCollection AddSharedServicesAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var bearerEnabled = !string.IsNullOrWhiteSpace(configuration[$"{SectionName}:Authority"]);

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CombinedScheme;
                options.DefaultChallengeScheme = CombinedScheme;
            })
            .AddPolicyScheme(CombinedScheme, "API key or bearer token", options =>
                options.ForwardDefaultSelector = context =>
                    !context.Request.Headers.ContainsKey(ApiKeyDefaults.Header)
                    && bearerEnabled
                    && context.Request.Headers.Authorization.Count > 0
                        ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                        : ApiKeyDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, displayName: null, configureOptions: null);

        if (bearerEnabled)
        {
            services.AddOpenIddict().AddValidation(options =>
            {
                options.UseIntrospection();
                options.UseSystemNetHttp();
                options.UseAspNetCore();
            });

            services.AddOptions<OpenIddictValidationOptions>().Configure<IConfiguration>((options, config) =>
            {
                options.Issuer = new Uri(config[$"{SectionName}:Authority"]!);
                options.ClientId = config[$"{SectionName}:ClientId"];
                options.ClientSecret = config[$"{SectionName}:ClientSecret"];
                if (!string.IsNullOrWhiteSpace(config[$"{SectionName}:Audience"]))
                    options.Audiences.Add(config[$"{SectionName}:Audience"]!);
            });
        }

        services.AddAuthorization(options =>
        {
            // Deny by default: anything without an explicit policy still needs an authenticated caller.
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(Policies.ConfigRead, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(ApiScopes.ConfigRead)));
            options.AddPolicy(Policies.ConfigWrite, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(ApiScopes.ConfigWrite)));
            options.AddPolicy(Policies.EmailSend, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(ApiScopes.EmailSend)));
            // "telegram:send" is the scope keys had before chat covered more than Telegram; it keeps working.
            options.AddPolicy(Policies.ChatSend, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(ApiScopes.ChatSend, ApiScopes.TelegramSend)));
            options.AddPolicy(Policies.StorageUpload, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(ApiScopes.StorageUpload)));
            options.AddPolicy(Policies.StorageRead, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(ApiScopes.StorageRead, ApiScopes.StorageUpload, ApiScopes.StorageDelete)));
            options.AddPolicy(Policies.NotificationSend, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(ApiScopes.EmailSend, ApiScopes.ChatSend, ApiScopes.TelegramSend)));
            options.AddPolicy(Policies.StorageDelete, policy => policy.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(ApiScopes.StorageDelete)));
        });
        services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActor, HttpContextCurrentActor>();
        return services;
    }
}

/// <summary>The caller as the audit log names it; "system" when there is no request (startup seeding).</summary>
public sealed class HttpContextCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public string Name
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true) return "system";

            var name = user.FindFirst("email")?.Value
                       ?? user.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                       ?? user.FindFirst("sub")?.Value
                       ?? "unknown";
            return name.Length > 100 ? name[..100] : name;
        }
    }
}
