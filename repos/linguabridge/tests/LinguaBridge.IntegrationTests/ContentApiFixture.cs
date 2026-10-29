using LinguaBridge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace LinguaBridge.IntegrationTests;

/// <summary>
/// The real API host on a real, freshly migrated PostgreSQL, with <see cref="TestAuthHandler"/> instead of the
/// STS. Callers are chosen per request with the <c>X-Test-*</c> headers, so one host serves admins, learners and
/// anonymous clients. Needs Docker.
/// </summary>
public sealed class ContentApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly Guid AdminId = Guid.Parse("a0a0a0a0-0000-4000-8000-000000000001");
    public static readonly Guid LearnerId = Guid.Parse("b1b1b1b1-0000-4000-8000-000000000002");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var context = new LinguaBridgeDbContext(
            new DbContextOptionsBuilder<LinguaBridgeDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await context.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:LinguaBridgeDb"] = _postgres.GetConnectionString(),
            }));

        builder.ConfigureTestServices(services =>
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
    }

    public HttpClient Anonymous() => CreateClient();

    public HttpClient Learner(params string[] roles) => As(LearnerId, roles);

    public HttpClient Admin() => As(AdminId, "GlobalAdmin");

    private HttpClient As(Guid userId, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }
}
