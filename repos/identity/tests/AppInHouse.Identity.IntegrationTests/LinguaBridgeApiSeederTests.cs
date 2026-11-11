using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using AppInHouse.Identity.Infrastructure.Persistence.Seeders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;

namespace AppInHouse.Identity.IntegrationTests;

/// <summary>The LinguaBridge API resource server is only seeded when its introspection secret is configured.</summary>
public class LinguaBridgeApiSeederTests
{
    private const string Secret = "integration-test-linguabridge-api-secret";

    [Fact]
    public async Task Seeder_CreatesScopeAndIntrospectionOnlyClient_WhenSecretConfigured()
    {
        await using var factory = new LinguaBridgeSeedingFactory(Secret);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var scopes = services.GetRequiredService<IOpenIddictScopeManager>();
        var apiScope = await scopes.FindByNameAsync("linguabridge_api");
        Assert.NotNull(apiScope);
        Assert.Contains("linguabridge_api", await scopes.GetResourcesAsync(apiScope));

        var applications = services.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await applications.FindByClientIdAsync("linguabridge_api");
        Assert.NotNull(client);
        Assert.Equal(OpenIddictConstants.ClientTypes.Confidential, await applications.GetClientTypeAsync(client));
        Assert.True(await applications.ValidateClientSecretAsync(client, Secret));

        // Introspection only: it must not be able to obtain tokens itself.
        var permissions = await applications.GetPermissionsAsync(client);
        Assert.Equal(OpenIddictConstants.Permissions.Endpoints.Introspection, Assert.Single(permissions));
    }

    [Fact]
    public async Task Seeder_SkipsScopeAndClient_WhenSecretEmpty()
    {
        await using var factory = new LinguaBridgeSeedingFactory(string.Empty);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.Null(await services.GetRequiredService<IOpenIddictScopeManager>().FindByNameAsync("linguabridge_api"));
        Assert.Null(await services.GetRequiredService<IOpenIddictApplicationManager>().FindByClientIdAsync("linguabridge_api"));
    }

    [Fact]
    public async Task Seeder_DoesNotDuplicateLinguaBridgeEntries_WhenRunTwice()
    {
        await using var factory = new LinguaBridgeSeedingFactory(Secret);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        await services.GetRequiredService<DbSeeder>().SeedAsync();

        // admin-portal, internal-service, shared-services-portal, identity_admin_api, linguabridge_api; and the six scopes (with roles) plus linguabridge_api.
        Assert.Equal(5, await services.GetRequiredService<IOpenIddictApplicationManager>().CountAsync());
        Assert.Equal(7, await services.GetRequiredService<IOpenIddictScopeManager>().CountAsync());
    }

    [Fact]
    public async Task Discovery_AdvertisesLinguaBridgeScope()
    {
        await using var factory = new LinguaBridgeSeedingFactory(Secret);
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/.well-known/openid-configuration");

        Assert.Contains("linguabridge_api", json);
    }

    /// <summary>The standard STS factory plus the LinguaBridge secret (the shared one deliberately leaves it unset).</summary>
    private sealed class LinguaBridgeSeedingFactory(string secret) : StsWebApplicationFactory
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Seeding:LinguaBridgeApiClientSecret"] = secret }));

            // Own database, so this does not share state with the base factory's.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.AddDbContext<AppIdentityDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            });
        }
    }
}
