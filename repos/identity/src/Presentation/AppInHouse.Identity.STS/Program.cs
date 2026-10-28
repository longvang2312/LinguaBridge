using AppInHouse.Identity.Infrastructure.Persistence;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using AppInHouse.Identity.Infrastructure.Persistence.Seeders;
using AppInHouse.Identity.STS.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// The provider is SQL Server; integration tests replace this registration.
var connectionString = configuration.GetConnectionString(SqlServerOptionsExtensions.ConnectionStringName);
builder.Services.AddDbContext<AppIdentityDbContext>(options => options.UseIdentitySqlServer<AppIdentityDbContext>(connectionString));
builder.Services.AddDbContext<AppAuthDbContext>(options => options.UseIdentitySqlServer<AppAuthDbContext>(connectionString));
builder.Services.AddDbContext<AppAuditDbContext>(options => options.UseIdentitySqlServer<AppAuditDbContext>(connectionString));

builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        // Use the OpenID Connect claim names so principals produced by Identity map straight onto tokens.
        options.ClaimsIdentity.UserNameClaimType = Claims.Name;
        options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
        options.ClaimsIdentity.RoleClaimType = Claims.Role;
        options.ClaimsIdentity.EmailClaimType = Claims.Email;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<AppIdentityDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

builder.Services.AddControllersWithViews();

builder.Services.AddOpenIddict()
    .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AppIdentityDbContext>())
    .AddServer(options =>
    {
        options.SetAuthorizationEndpointUris("/connect/authorize")
               .SetTokenEndpointUris("/connect/token")
               .SetUserinfoEndpointUris("/connect/userinfo")
               .SetLogoutEndpointUris("/connect/logout")
               .SetIntrospectionEndpointUris("/connect/introspect");

        // Refresh tokens are rotated by default (every use issues a new one and the old one is redeemed).
        options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange()
               .AllowClientCredentialsFlow()
               .AllowRefreshTokenFlow();

        // Advertised in the discovery document (the scope store is only used for validation).
        options.RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.OfflineAccess, DbSeeder.AdminApiScope);

        var encryptionThumbprint = configuration["OpenIddict:EncryptionCertificateThumbprint"];
        var signingThumbprint = configuration["OpenIddict:SigningCertificateThumbprint"];
        if (!string.IsNullOrWhiteSpace(encryptionThumbprint) && !string.IsNullOrWhiteSpace(signingThumbprint))
        {
            options.AddEncryptionCertificate(encryptionThumbprint)
                   .AddSigningCertificate(signingThumbprint);
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Development/Test only: self-generated certificates stored in the current user's certificate store.
            options.AddDevelopmentEncryptionCertificate()
                   .AddDevelopmentSigningCertificate();
        }
        else
        {
            throw new InvalidOperationException(
                "OpenIddict:EncryptionCertificateThumbprint and OpenIddict:SigningCertificateThumbprint must be configured outside Development.");
        }

        options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableUserinfoEndpointPassthrough()
               .EnableLogoutEndpointPassthrough()
               .EnableStatusCodePagesIntegration();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// Read lazily so configuration added after startup (e.g. by tests) is honoured. Only enable it for local HTTP.
builder.Services.Configure<OpenIddictServerAspNetCoreOptions>(options =>
    options.DisableTransportSecurityRequirement = configuration.GetValue<bool>("OpenIddict:DisableTransportSecurityRequirement"));

builder.Services.Configure<DbSeederOptions>(configuration.GetSection(DbSeederOptions.SectionName));
builder.Services.AddScoped<DbSeeder>();
builder.Services.AddHostedService<StartupInitializer>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Text("AppInHouse Identity STS"));
app.MapControllers();
app.MapDefaultControllerRoute();

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
