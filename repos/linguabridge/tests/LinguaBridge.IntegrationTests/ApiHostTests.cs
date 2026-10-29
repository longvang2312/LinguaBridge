using System.Net;
using System.Text.Json;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Application.Messaging;
using LinguaBridge.Application.Modules;
using LinguaBridge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LinguaBridge.IntegrationTests;

public class ApiHostTests(ApiHostTests.LinguaBridgeApiFactory factory) : IClassFixture<ApiHostTests.LinguaBridgeApiFactory>
{
    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("LinguaBridge.Api", body.RootElement.GetProperty("module").GetString());
        Assert.True(body.RootElement.GetProperty("timestamp").TryGetDateTimeOffset(out _));
    }

    [Fact]
    public async Task Swagger_document_carries_the_api_title()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("LinguaBridge Modular Monolith API", body.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public void Host_container_resolves_every_module_and_infrastructure_service()
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var modules = services.GetServices<IModuleDescriptor>().Select(m => m.Name).Order().ToArray();
        Assert.Equal(["AiTutor", "Billing", "Content", "Learning"], modules);
        Assert.NotNull(services.GetRequiredService<IEventBus>());
        Assert.NotNull(services.GetRequiredService<IDateTimeProvider>());
        Assert.NotNull(services.GetRequiredService<PersistenceOptions>());

        // Resolving does not connect: the host must start even when PostgreSQL is down.
        var dbContext = services.GetRequiredService<LinguaBridgeDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", dbContext.Database.ProviderName);
    }

    /// <summary>Runs the real host in Development (Swagger on) and validates the whole container at build time.</summary>
    public sealed class LinguaBridgeApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
        }
    }
}
