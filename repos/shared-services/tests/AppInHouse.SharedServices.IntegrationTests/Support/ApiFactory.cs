using AppInHouse.SharedServices.Api;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Infrastructure.Storage;
using AppInHouse.SharedServices.Domain;
using System.Net;
using AppInHouse.SharedServices.Infrastructure.Notifications.Bots;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Infrastructure.Email;
using AppInHouse.SharedServices.Infrastructure.Webhooks;
using AppInHouse.SharedServices.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.IntegrationTests.Support;

/// <summary>
/// The real API host in memory, on its own SQLite file and key-ring folder (both deleted afterwards), with the development seeding
/// off so a test starts from an empty database and creates the API keys it needs.
/// </summary>
public class ApiFactory : WebApplicationFactory<ApiEntryPoint>
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ss-api-" + Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_folder, "test.db");

    /// <summary>The folder every local-disk storage configuration of this host puts its files under (deleted with the host).</summary>
    public string StoragePath => Path.Combine(_folder, "storage");

    /// <summary>Settings a test host adds to (or replaces in) the ones every host gets, for example another private-network switch.</summary>
    protected virtual IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>();

    /// <summary>"Testing" by default. In Development minimal APIs throw on a request that cannot be read instead of answering 400 themselves.</summary>
    protected virtual string EnvironmentName => "Testing";

    /// <summary>What the service sent to "SendGrid": the real HTTP client is replaced by this recorder.</summary>
    public RecordingHandler SendGrid { get; } = new();

    /// <summary>Stands in for the applications that subscribed to webhooks: every outbound webhook the service posted is recorded here.</summary>
    public RecordingHandler Webhook { get; } = new() { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) };

    /// <summary>Stands in for api.telegram.org, discord.com and hooks.slack.com: every chat request the service made is recorded here.</summary>
    public RecordingHandler Chat { get; } = new() { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":77}}") } };

    /// <summary>Stands in for an S3-compatible service (https://s3.example.test): every request the service made is recorded here.</summary>
    public RecordingHandler S3 { get; } = new() { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) };

    /// <summary>The in-memory store of the mock storage provider.</summary>
    public MockStorageProvider MockStorage => Services.GetServices<IStorageProvider>().OfType<MockStorageProvider>().Single();

    /// <summary>The in-memory mailbox of the mock email provider.</summary>
    public MockEmailProvider MockMail => Services.GetServices<IEmailProvider>().OfType<MockEmailProvider>().Single();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:SharedServicesDb"] = $"Data Source={DatabasePath}",
            ["Database:Provider"] = "Sqlite",
            ["Database:MigrateOnStartup"] = "true",
            ["DataProtection:KeysPath"] = Path.Combine(_folder, "keys"),
            ["SharedServices:Seeding:Enabled"] = "false",
            ["Authentication:Authority"] = string.Empty,
            ["Email:AllowPrivateNetworkHosts"] = "true",   // the fake SMTP server is on the loopback address
            ["Storage:BasePath"] = StoragePath,

            // The job engine without waiting: retries at once, no timer-driven tasks (a test runs a task by hand), metrics never reused.
            ["Jobs:RetryDelaysSeconds:0"] = "0",
            ["Jobs:RetryDelaysSeconds:1"] = "0",
            ["Jobs:RetryDelaysSeconds:2"] = "0",
            ["Jobs:SchedulesEnabled"] = "false",
            ["Jobs:MetricsCacheSeconds"] = "0",

            // A member who is offline hears of a message after one second here (20 in production).
            ["Chat:OfflineGraceSeconds"] = "1",
        };

        foreach (var (key, value) in ExtraSettings) settings[key] = value;   // a test host can change any of the above

        // Set both ways: which one the host reads first differs between hosting models.
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<SendGridEmailProvider>().ConfigurePrimaryHttpMessageHandler(() => SendGrid);
            services.AddHttpClient<TelegramBotProvider>().ConfigurePrimaryHttpMessageHandler(() => Chat);
            services.AddHttpClient<IWebhookSender, HttpWebhookSender>().ConfigurePrimaryHttpMessageHandler(() => Webhook);
            services.AddHttpClient<DiscordWebhookBotProvider>().ConfigurePrimaryHttpMessageHandler(() => Chat);
            services.AddHttpClient<SlackWebhookBotProvider>().ConfigurePrimaryHttpMessageHandler(() => Chat);
            services.AddSingleton<Amazon.Runtime.HttpClientFactory>(new StubS3HttpClientFactory(S3));
            services.AddSingleton<IHostAddressResolver>(new PublicHostResolver());   // no real DNS: the webhook hosts "resolve" to a public address
        });
    }

    /// <summary>Stores a new API key with these scopes and returns it in plain text, as the one call that creates a key would.</summary>
    public async Task<(string PlainKey, ApiKey Key)> CreateKeyAsync(params string[] scopes) =>
        await CreateKeyAsync(expiresAt: null, scopes);

    public async Task<(string PlainKey, ApiKey Key)> CreateKeyAsync(DateTimeOffset? expiresAt, params string[] scopes) =>
        await CreateKeyForProjectAsync("default", expiresAt, scopes);

    /// <summary>A key that belongs to <paramref name="project"/> ("default" is the platform's own and may act for every project).</summary>
    public async Task<(string PlainKey, ApiKey Key)> CreateKeyForProjectAsync(string project, DateTimeOffset? expiresAt, params string[] scopes)
    {
        using var scope = Services.CreateScope();
        var (key, plain) = ApiKey.Generate(project, "Integration test", scopes, expiresAt, DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<IApiKeyRepository>().AddAsync(key);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return (plain, key);
    }

    public async Task<T> WithDbAsync<T>(Func<SharedServicesDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>());
    }

    public HttpClient ClientWithKey(string? plainKey)
    {
        var client = CreateClient();
        if (plainKey is not null) client.DefaultRequestHeaders.Add("X-Api-Key", plainKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        SqliteConnection.ClearAllPools();   // the file stays locked while a pooled connection is open
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed right now is not a test failure.
        }
    }
}

/// <summary>The same host in the Development environment (without its seeding), where a request that cannot be read is thrown, not answered.</summary>
public sealed class DevelopmentApiFactory : ApiFactory
{
    protected override string EnvironmentName => "Development";
}

/// <summary>Answers every name with a public address, so a test needs no network.</summary>
public sealed class PublicHostResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(IPAddress.TryParse(host, out var literal) ? new[] { literal }
            : host.StartsWith("evil-", StringComparison.Ordinal) ? new[] { IPAddress.Parse("169.254.169.254") }   // a wildcard DNS name that points at the metadata address
            : new[] { IPAddress.Parse("93.184.216.34") });
}

/// <summary>Gives the S3 client of the service an HTTP layer that goes nowhere but into a recorder.</summary>
public sealed class StubS3HttpClientFactory(HttpMessageHandler handler) : Amazon.Runtime.HttpClientFactory
{
    public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig clientConfig) => new(handler, disposeHandler: false);
}

/// <summary>Storage may reach private networks, mail may not.</summary>
public sealed class StoragePrivateOnlyApiFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["Storage:AllowPrivateNetworkHosts"] = "true", ["Email:AllowPrivateNetworkHosts"] = "false",
    };
}

/// <summary>Mail may reach private networks, storage may not.</summary>
public sealed class EmailPrivateOnlyApiFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["Storage:AllowPrivateNetworkHosts"] = "false", ["Email:AllowPrivateNetworkHosts"] = "true",
    };
}

/// <summary>A host that allows an absolute folder for local-disk storage.</summary>
public sealed class AbsolutePathsApiFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["Storage:AllowAbsolutePaths"] = "true" };
}
