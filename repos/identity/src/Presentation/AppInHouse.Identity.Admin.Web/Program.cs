using AppInHouse.Identity.Admin.Web.Infrastructure;
using AppInHouse.Identity.Application;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Infrastructure.Caching;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var connectionString = configuration.GetConnectionString(SqlServerOptionsExtensions.ConnectionStringName);

// The identity platform's database (shared with the STS and the Admin API).
builder.Services.AddDbContext<AppIdentityDbContext>(options => options.UseIdentitySqlServer<AppIdentityDbContext>(connectionString));
builder.Services.AddDbContext<AppAuthDbContext>(options => options.UseIdentitySqlServer<AppAuthDbContext>(connectionString));
builder.Services.AddDbContext<AppAuditDbContext>(options => options.UseIdentitySqlServer<AppAuditDbContext>(connectionString));

// UserManager / RoleManager and the OpenIddict application/scope managers used by the admin use cases.
builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        options.ClaimsIdentity.UserNameClaimType = Claims.Name;
        options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
        options.ClaimsIdentity.RoleClaimType = Claims.Role;
        options.ClaimsIdentity.EmailClaimType = Claims.Email;
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<AppIdentityDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddOpenIddict()
    .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AppIdentityDbContext>());

// Sign-in: the STS (OpenID Connect, Authorization Code + PKCE, public client "admin-portal") with a local cookie session.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "AppInHouse.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.AccessDeniedPath = "/Home/AccessDenied";
    })
    .AddOpenIdConnect(options =>
    {
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.Scope.Clear();
        options.Scope.Add(Scopes.OpenId);
        options.Scope.Add(Scopes.Profile);
        options.Scope.Add(Scopes.Email);
        options.TokenValidationParameters.NameClaimType = Claims.Name;
        options.TokenValidationParameters.RoleClaimType = Claims.Role;
    });

// Read lazily so the settings can come from any configuration source at runtime.
builder.Services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme).Configure<IConfiguration>((options, config) =>
{
    options.Authority = config["Authentication:Authority"];
    options.ClientId = config["Authentication:ClientId"];
    if (!string.IsNullOrWhiteSpace(config["Authentication:ClientSecret"]))
        options.ClientSecret = config["Authentication:ClientSecret"];
});

builder.Services.AddAuthorization(options =>
{
    // Deny by default: anything without an explicit policy still needs a signed-in user.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// Authorization engine + admin use cases (the same ones the Admin API exposes). What the signed-in user may do is decided
// there, from the database, on every operation; the portal only hides what would be refused.
builder.Services.AddPermissionPersistence();
builder.Services.AddPermissionCaching();
builder.Services.AddAuthorizationEngine();
builder.Services.AddAdminAuthorization();
builder.Services.AddAdminServices();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentCaller, HttpCurrentCaller>();

builder.Services.AddHostedService<DatabaseMigrator>(); // does nothing unless Database:MigrateOnStartup is true

builder.Services.AddControllersWithViews(options =>
{
    // Every state-changing request needs a valid antiforgery token (the POST actions also say so explicitly).
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<PortalExceptionFilter>();
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "AppInHouse.Admin.Antiforgery";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

var app = builder.Build();

// Fail fast instead of answering every request with a 500.
if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Authentication:Authority"]))
    throw new InvalidOperationException("Authentication:Authority (the STS base URL) must be configured.");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    // The sign-in handler's own cookies (correlation, nonce) are "Secure; SameSite=None" by default. A browser drops a Secure
    // cookie on plain HTTP (other than on localhost), so signing in over http://<ip>:5000 fails with "Correlation failed".
    // SameSite=Lax would fix that but then fails on https://localhost:44303: the STS answers with a cross-site POST
    // (form_post) and http://localhost and https://localhost are different sites. So the policy follows the request scheme.
    static bool IsSignInCookie(string name) =>
        name.StartsWith(".AspNetCore.OpenIdConnect.", StringComparison.Ordinal) || name.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal);

    app.UseCookiePolicy(new CookiePolicyOptions
    {
        OnAppendCookie = context =>
        {
            if (!IsSignInCookie(context.CookieName)) return;
            context.CookieOptions.Secure = context.Context.Request.IsHttps;
            context.CookieOptions.SameSite = context.Context.Request.IsHttps ? SameSiteMode.None : SameSiteMode.Lax;
        },
        OnDeleteCookie = context =>
        {
            if (!IsSignInCookie(context.CookieName)) return;
            context.CookieOptions.Secure = context.Context.Request.IsHttps;
            context.CookieOptions.SameSite = context.Context.Request.IsHttps ? SameSiteMode.None : SameSiteMode.Lax;
        },
    });
}

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
