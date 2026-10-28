using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppInHouse.Identity.IntegrationTests;

/// <summary>Hosts the STS in-process against an isolated EF Core in-memory database, seeded at startup.</summary>
public class StsWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@appinhouse.local";
    public const string AdminPassword = "Admin@AppInHouse123!";
    public const string InternalServiceSecret = "integration-test-internal-service-secret";
    public const string AdminApiClientSecret = "integration-test-admin-api-secret";
    public const string AdminPortalRedirectUri = "https://localhost:7111/signin-oidc";

    private readonly string _databaseName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:EnsureCreated"] = "true",
                ["OpenIddict:DisableTransportSecurityRequirement"] = "true",
                ["Seeding:Enabled"] = "true",
                ["Seeding:AdminEmail"] = AdminEmail,
                ["Seeding:AdminPassword"] = AdminPassword,
                ["Seeding:InternalServiceSecret"] = InternalServiceSecret,
                ["Seeding:AdminApiClientSecret"] = AdminApiClientSecret,
                ["Seeding:AdminPortalRedirectUris:0"] = AdminPortalRedirectUri,
                ["Seeding:AdminPortalPostLogoutRedirectUris:0"] = "https://localhost:7111/signout-callback-oidc",
            }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<AppIdentityDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}
