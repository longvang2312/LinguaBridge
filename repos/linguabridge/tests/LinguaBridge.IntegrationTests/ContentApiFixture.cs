using LinguaBridge.Infrastructure.Persistence;
using LinguaBridge.Modules.AiTutor.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>The AI model. The real providers are replaced so no test calls (or pays for) OpenAI.</summary>
    public FakeLlm Llm { get; } = new();

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
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.RemoveAll<ILlmProvider>();
            services.AddSingleton<ILlmProvider>(Llm);
        });
    }

    public HttpClient Anonymous() => CreateClient();

    public HttpClient Learner(params string[] roles) => As(LearnerId, roles);

    public HttpClient Admin() => As(AdminId, "GlobalAdmin");

    /// <summary>A specific learner, so a test can run as two different people.</summary>
    public HttpClient LearnerAs(Guid userId) => As(userId);

    /// <summary>A token whose subject is not a user id, like the one a service gets from client_credentials.</summary>
    public HttpClient WithSubject(string subject)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, subject);
        return client;
    }

    private HttpClient As(Guid userId, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }
}

/// <summary>A model that answers from a script and remembers what it was asked.</summary>
public sealed class FakeLlm : ILlmProvider
{
    public const string JsonReply = """{"reply_text":"Tell me about yourself.","pinyin_or_ipa":"/tel mi/","translation":"Hay ke ve ban than.","grammar_tips":"Good start."}""";

    private readonly List<LlmRequest> _requests = [];

    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    /// <summary>What the model says next; replace it to script a plain-text or failing model, and restore it afterwards.</summary>
    public Func<LlmRequest, LlmResponse> Respond { get; set; } = _ => new LlmResponse(JsonReply, 120, 30, 0.00004m, "OpenAI_gpt-4o-mini");

    /// <summary>How long the model takes to answer; a slow model is what lets concurrent requests overlap.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        lock (_requests)
            _requests.Add(request);
        if (Latency > TimeSpan.Zero)
            await Task.Delay(Latency, ct);
        return Respond(request);
    }
}
