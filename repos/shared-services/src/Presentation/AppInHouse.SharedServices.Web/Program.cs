using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Security.Claims;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSharedServicesClient(options =>
    builder.Configuration.GetSection(SharedServicesClientOptions.SectionName).Bind(options));

// Razor Pages validate the anti-forgery token of every POST by default. The cookie is for this site only and never sent along
// with a request that comes from another one.
builder.Services.AddRazorPages();

// The default HTML encoder writes every character outside Basic Latin as an entity, which turns Vietnamese text and the secret
// mask into "&#x1EA5;" and "&#x2022;". Allowing all of Unicode keeps the page source readable; the characters that matter to
// HTML (< > & " ') are still escaped.
builder.Services.AddWebEncoders(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "AppInHouse.SharedServices.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// The anti-forgery keys live in the Data Protection ring; keeping it in a folder (a volume in a container) means a restart does
// not invalidate the forms that are open.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "AppInHouse.SharedServices.Web");
if (!string.IsNullOrWhiteSpace(keysPath))
{
    dataProtection.PersistKeysToFileSystem(Directory.CreateDirectory(Path.GetFullPath(keysPath, builder.Environment.ContentRootPath)));

    // Keys in a folder are plain XML unless something encrypts them: DPAPI on Windows; elsewhere protect the folder itself.
    if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
}

// ---------------------------------------------------------------------------------------------------------- sign-in
// Every page needs a signed-in administrator. The user signs in at AppInHouse Identity (OpenID Connect, authorization code +
// PKCE) and the portal keeps its own session in an encrypted cookie. With Authentication:Enabled=false (local development) a
// fixed administrator is signed in instead, so the portal works without the identity server.
var authentication = PortalAuthenticationOptions.From(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(authentication);

if (authentication.Enabled)
{
    builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = "AppInHouse.SharedServices.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = authentication.CookieSecurePolicy;
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = authentication.Authority;
            options.ClientId = authentication.ClientId;
            if (!string.IsNullOrEmpty(authentication.ClientSecret)) options.ClientSecret = authentication.ClientSecret;
            options.RequireHttpsMetadata = authentication.RequireHttpsMetadata;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;

            // The answer comes back in the query string, so the short-lived correlation cookies can be SameSite=Lax; the
            // default (a form post) needs SameSite=None, which browsers refuse without HTTPS.
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = authentication.CookieSecurePolicy;
            options.NonceCookie.SecurePolicy = authentication.CookieSecurePolicy;

            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
            options.SignedOutRedirectUri = "/Account/Logout?signedOut=true";
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

            options.Scope.Clear();
            foreach (var scope in new[] { "openid", "profile", "email", "roles" }) options.Scope.Add(scope);

            // Keep the claim names as the identity server sends them ("name", "email", "role").
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "role";

            // The ID token is kept for the sign-out request (id_token_hint); the access and refresh tokens are not needed
            // (the portal calls the API with its own key) and would only make the cookie bigger.
            options.SaveTokens = true;
            options.Events.OnTicketReceived = context =>
            {
                var idToken = context.Properties?.GetTokenValue("id_token");
                context.Properties?.StoreTokens(idToken is null ? [] : [new AuthenticationToken { Name = "id_token", Value = idToken }]);
                return Task.CompletedTask;
            };

            // A refused or failed sign-in (the user cancelled, the identity server is down) ends on a page that says so,
            // instead of looping back to the identity server or showing a stack trace.
            options.Events.OnRemoteFailure = context =>
            {
                context.Response.Redirect("/Account/Login?error=1");
                context.HandleResponse();
                return Task.CompletedTask;
            };
        });
}
else
{
    builder.Services.AddAuthentication(DevelopmentAdministratorHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAdministratorHandler>(DevelopmentAdministratorHandler.SchemeName, null);
}

builder.Services.AddAuthorization(options =>
{
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();
    if (!string.IsNullOrWhiteSpace(authentication.RequiredRole)) policy.RequireRole(authentication.RequiredRole);
    options.FallbackPolicy = policy.Build();
});

var app = builder.Build();

if (!authentication.Enabled)
    app.Logger.LogWarning("Authentication:Enabled is false: every visitor is signed in as {User}. Use this for local development only.", DevelopmentAdministratorHandler.Email);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// For the container health check: the portal answers, without calling the API (a portal that is up while the API is down is still up).
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", module = "AppInHouse.SharedServices.Web" })).AllowAnonymous();

app.Run();

namespace AppInHouse.SharedServices.Web
{
    /// <summary>The <c>Authentication</c> section: whether sign-in is enforced, where the identity server is and who may enter.</summary>
    public sealed class PortalAuthenticationOptions
    {
        public bool Enabled { get; init; } = true;
        public string Authority { get; init; } = string.Empty;
        public string ClientId { get; init; } = string.Empty;
        public string ClientSecret { get; init; } = string.Empty;
        public bool RequireHttpsMetadata { get; init; } = true;
        public string RequiredRole { get; init; } = "GlobalAdmin";
        public CookieSecurePolicy CookieSecurePolicy { get; init; } = CookieSecurePolicy.Always;

        /// <summary>Reads and checks the section; a portal that would start without a usable sign-in does not start.</summary>
        public static PortalAuthenticationOptions From(IConfiguration configuration, IHostEnvironment environment)
        {
            var section = configuration.GetSection("Authentication");
            var enabled = section.GetValue("Enabled", true);

            // Cookies are marked Secure outside development. A deployment that is plain HTTP on a trusted network (the browser
            // would never send such a cookie back) has to say so with SecureCookies=false.
            var secure = section.GetValue("SecureCookies", !environment.IsDevelopment());
            var options = new PortalAuthenticationOptions
            {
                Enabled = enabled,
                Authority = (section["Authority"] ?? string.Empty).Trim(),
                ClientId = (section["ClientId"] ?? string.Empty).Trim(),
                ClientSecret = section["ClientSecret"] ?? string.Empty,
                RequireHttpsMetadata = section.GetValue("RequireHttpsMetadata", true),
                RequiredRole = (section["RequiredRole"] ?? "GlobalAdmin").Trim(),
                CookieSecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest,
            };

            if (!enabled) return options;
            if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) || authority.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Authentication:Authority must be the address of the identity server (http:// or https://), or set Authentication:Enabled to false.");
            if (options.ClientId.Length == 0)
                throw new InvalidOperationException("Authentication:ClientId is required when Authentication:Enabled is true.");
            if (options.RequireHttpsMetadata && authority.Scheme != "https")
                throw new InvalidOperationException("Authentication:Authority must use https (or set Authentication:RequireHttpsMetadata to false on a trusted network).");
            return options;
        }
    }

    /// <summary>The development stand-in for the identity server: every request is a signed-in administrator.</summary>
    public sealed class DevelopmentAdministratorHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder, PortalAuthenticationOptions portal)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "DevelopmentAdministrator";
        public const string Email = "dev-admin@appinhouse.local";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new("sub", "dev-admin"), new("name", "Dev Admin"), new("email", Email) };
            if (!string.IsNullOrWhiteSpace(portal.RequiredRole)) claims.Add(new Claim("role", portal.RequiredRole));
            var identity = new ClaimsIdentity(claims, SchemeName, "name", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
