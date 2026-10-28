using System.Text.Json.Serialization;
using AppInHouse.Identity.Admin.Api.Infrastructure;
using AppInHouse.Identity.Application;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Infrastructure.Caching;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OpenIddict.Validation;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// The identity platform's database (shared with the STS). Provider: SQL Server; integration tests replace these.
builder.Services.AddDbContext<AppIdentityDbContext>(options => options.UseIdentitySqlServer<AppIdentityDbContext>(configuration.GetConnectionString("IdentityDb")));
builder.Services.AddDbContext<AppAuthDbContext>(options => options.UseIdentitySqlServer<AppAuthDbContext>(configuration.GetConnectionString("IdentityDb")));
builder.Services.AddDbContext<AppAuditDbContext>(options => options.UseIdentitySqlServer<AppAuditDbContext>(configuration.GetConnectionString("IdentityDb")));

// UserManager / RoleManager (no cookies or sign-in: this is an API) and the OpenIddict application/scope managers.
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
    .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AppIdentityDbContext>())
    .AddValidation(options =>
    {
        // Access tokens are issued by the STS and are validated by asking it (introspection).
        options.UseIntrospection();
        options.UseSystemNetHttp();
        options.UseAspNetCore();
    });

// Read lazily so the settings can come from any configuration source at runtime.
builder.Services.AddOptions<OpenIddictValidationOptions>().Configure<IConfiguration>((options, config) =>
{
    var authority = config["Authentication:Authority"];
    if (!string.IsNullOrWhiteSpace(authority))
        options.Issuer = new Uri(authority);

    options.ClientId = config["Authentication:ClientId"];
    options.ClientSecret = config["Authentication:ClientSecret"];

    var audience = config["Authentication:Audience"];
    if (!string.IsNullOrWhiteSpace(audience))
        options.Audiences.Add(audience);
});

builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddAuthorization(options =>
{
    // Deny by default: anything without an explicit policy still needs an authenticated caller.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// Authorization engine + admin use cases. Invalidation bumps the shared version, so every API node notices.
builder.Services.AddPermissionPersistence();
builder.Services.AddPermissionCaching();
builder.Services.AddAuthorizationEngine();
builder.Services.AddAdminAuthorization();
builder.Services.AddAdminServices();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentCaller, HttpCurrentCaller>();

builder.Services.AddExceptionHandler<AdminExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddControllers(options => options.Filters.Add<AdminExceptionFilter>())
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AppInHouse Identity Admin API",
        Version = "v1",
        Description = "Administration of projects, OpenID Connect clients, users, roles, permissions and assignments, with an audit trail.",
    });
    options.EnableAnnotations();

    var bearer = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token issued by the STS for the identity_admin_api scope.",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    options.AddSecurityDefinition("Bearer", bearer);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = Array.Empty<string>() });
});

var app = builder.Build();

// Fail fast instead of answering every request with a 500 (the validation handler needs an issuer to work at all).
if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["Authentication:Authority"]))
    throw new InvalidOperationException("Authentication:Authority (the STS base URL) must be configured.");

app.UseExceptionHandler();

if (app.Environment.IsDevelopment() || configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Identity Admin API v1"));
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
