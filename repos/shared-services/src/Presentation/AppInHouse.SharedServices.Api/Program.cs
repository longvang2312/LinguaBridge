using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Api.Endpoints;
using AppInHouse.SharedServices.Api.Errors;
using AppInHouse.SharedServices.Api.Seeding;
using AppInHouse.SharedServices.Application;
using AppInHouse.SharedServices.Infrastructure;
using AppInHouse.SharedServices.Persistence;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSharedServicesApplication();
builder.Services.AddSharedServicesInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSharedServicesPersistence(builder.Configuration);
builder.Services.AddSharedServicesAuthentication(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<SharedServicesExceptionHandler>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Keeps the mask readable ("••••••••") instead of "•..." in the JSON; the responses are not embedded in HTML.
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AppInHouse Shared Services API",
        Version = "v1",
        Description = "Email, Telegram and storage settings shared by the AppInHouse projects, with versioned, rollback-able configuration.",
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyDefaults.Header,
        Description = "The API key of the calling project (aih_live_...).",
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", document)] = [],
    });
});

var app = builder.Build();

// Opt-in (on in the development settings): creates or updates the schema before the host starts listening, so "port open"
// means "database ready". A failure stops the start.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.Services.InitializeSharedServicesDatabaseAsync();
    if (app.Environment.IsDevelopment()) await DevelopmentSeeder.SeedAsync(app.Services, app.Configuration);
}

app.UseExceptionHandler();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "AppInHouse Shared Services API v1"));
}

// Swagger (above) is middleware, so the fallback policy never sees it; endpoints below are protected by default.
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", (TimeProvider clock) => Results.Ok(new HealthResponse("Healthy", "AppInHouse.SharedServices.Api", clock.GetUtcNow())))
    .WithName("GetHealth")
    .WithTags("Health")
    .AllowAnonymous();

app.MapChannelConfigurationEndpoints();
app.MapEmailEndpoints();
app.MapChatEndpoints();
app.MapStorageEndpoints();

app.Run();

internal sealed record HealthResponse(string Status, string Module, DateTimeOffset Timestamp);
