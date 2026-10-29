using LinguaBridge.Api.Auth;
using LinguaBridge.Api.Endpoints;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Infrastructure.External;
using LinguaBridge.Infrastructure.Persistence;
using LinguaBridge.Modules.AiTutor;
using LinguaBridge.Modules.Billing;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Learning;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Modules never reference each other; the host is the only place that sees all of them.
builder.Services.AddContentModule(configuration);
builder.Services.AddLearningModule(configuration);
builder.Services.AddAiTutorModule(configuration);
builder.Services.AddBillingModule(configuration);
builder.Services.AddInfrastructurePersistence(configuration);
builder.Services.AddInfrastructureExternal(configuration);
builder.Services.AddLinguaBridgeAuthentication();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "LinguaBridge Modular Monolith API",
        Version = "v1",
        Description = "Backend for LinguaBridge: English and Chinese learning with business communication.",
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token issued by the Identity STS for the linguabridge_api scope.",
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
    });
});

var app = builder.Build();

app.Configuration.EnsureAuthenticationConfigured(app.Environment);

if (app.Environment.IsDevelopment() || configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "LinguaBridge Modular Monolith API v1"));
}

app.UseHttpsRedirection();

// Swagger (above) is middleware, so the fallback policy never sees it; endpoints below are protected by default.
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", (IDateTimeProvider clock) => Results.Ok(new HealthResponse("Healthy", "LinguaBridge.Api", clock.UtcNow)))
    .WithName("GetHealth")
    .WithTags("Health")
    .AllowAnonymous();

app.MapMeEndpoints();
app.MapContentEndpoints();
app.MapLearningEndpoints();
app.MapAiTutorEndpoints();
app.MapBillingEndpoints();

app.Run();

internal sealed record HealthResponse(string Status, string Module, DateTimeOffset Timestamp);
