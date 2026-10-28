using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence.Seeders;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace AppInHouse.Identity.IntegrationTests;

public class DbSeederTests : IClassFixture<StsWebApplicationFactory>
{
    private readonly StsWebApplicationFactory _factory;

    public DbSeederTests(StsWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Seeder_CreatesAdminRoleScopesAndClients()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(StsWebApplicationFactory.AdminEmail);
        Assert.NotNull(admin);
        Assert.True(admin.EmailConfirmed);
        Assert.True(admin.IsActive);
        Assert.True(await users.IsInRoleAsync(admin, "GlobalAdmin"));
        Assert.True(await users.CheckPasswordAsync(admin, StsWebApplicationFactory.AdminPassword));

        var scopes = services.GetRequiredService<IOpenIddictScopeManager>();
        foreach (var name in new[] { "openid", "profile", "email", "offline_access", "identity_admin_api" })
            Assert.NotNull(await scopes.FindByNameAsync(name));

        var applications = services.GetRequiredService<IOpenIddictApplicationManager>();
        var portal = await applications.FindByClientIdAsync("admin-portal");
        Assert.NotNull(portal);
        Assert.Equal(OpenIddictConstants.ClientTypes.Public, await applications.GetClientTypeAsync(portal));
        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, await applications.GetRequirementsAsync(portal));

        var service = await applications.FindByClientIdAsync("internal-service");
        Assert.NotNull(service);
        Assert.Equal(OpenIddictConstants.ClientTypes.Confidential, await applications.GetClientTypeAsync(service));
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials, await applications.GetPermissionsAsync(service));
    }

    [Fact]
    public async Task Seeder_IsIdempotent()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var seeder = services.GetRequiredService<DbSeeder>();

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(1, users.Users.Count(u => u.Email == StsWebApplicationFactory.AdminEmail));
        Assert.Single(await users.GetUsersInRoleAsync("GlobalAdmin"));

        Assert.Equal(3, await services.GetRequiredService<IOpenIddictApplicationManager>().CountAsync());
        Assert.Equal(5, await services.GetRequiredService<IOpenIddictScopeManager>().CountAsync());
        Assert.Equal(1, services.GetRequiredService<RoleManager<ApplicationRole>>().Roles.Count());
    }
}
