using LinguaBridge.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;

namespace LinguaBridge.Api.Auth;

public static class AuthenticationExtensions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// Access tokens are issued (and encrypted) by the Identity STS, so they are validated by asking it
    /// (introspection) - the same model as the Identity Admin API. Every endpoint requires an authenticated
    /// caller unless it opts out with <c>AllowAnonymous</c>.
    /// </summary>
    public static IServiceCollection AddLinguaBridgeAuthentication(this IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddValidation(options =>
            {
                options.UseIntrospection();
                options.UseSystemNetHttp();
                options.UseAspNetCore();
            });

        // Read lazily so the settings can come from any configuration source at runtime.
        services.AddOptions<OpenIddictValidationOptions>().Configure<IConfiguration>((options, config) =>
        {
            var authority = config[$"{SectionName}:Authority"];
            if (!string.IsNullOrWhiteSpace(authority))
                options.Issuer = new Uri(authority);

            options.ClientId = config[$"{SectionName}:ClientId"];
            options.ClientSecret = config[$"{SectionName}:ClientSecret"];

            var audience = config[$"{SectionName}:Audience"];
            if (!string.IsNullOrWhiteSpace(audience))
                options.Audiences.Add(audience);
        });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        services.AddAuthorization(options =>
        {
            // Deny by default: anything without an explicit policy still needs an authenticated caller.
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        return services;
    }

    /// <summary>
    /// Without an issuer or a secret the validation handler cannot work at all; fail at startup rather than with a
    /// 500 per request. Called on the built app, because that is where every configuration source (including
    /// overrides applied by the host or tests) has been merged. Development is exempt so the repo runs out of the box.
    /// </summary>
    public static void EnsureAuthenticationConfigured(this IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
            return;

        if (string.IsNullOrWhiteSpace(configuration[$"{SectionName}:Authority"]))
            throw new InvalidOperationException("Authentication:Authority (the STS base URL) must be configured.");
        if (string.IsNullOrWhiteSpace(configuration[$"{SectionName}:ClientSecret"]))
            throw new InvalidOperationException("Authentication:ClientSecret (the linguabridge_api introspection secret) must be configured.");
    }
}
