using AppInHouse.SharedServices.Api;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Domain;
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
public sealed class ApiFactory : WebApplicationFactory<ApiEntryPoint>
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ss-api-" + Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_folder, "test.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:SharedServicesDb"] = $"Data Source={DatabasePath}",
            ["Database:Provider"] = "Sqlite",
            ["Database:MigrateOnStartup"] = "true",
            ["DataProtection:KeysPath"] = Path.Combine(_folder, "keys"),
            ["SharedServices:Seeding:Enabled"] = "false",
            ["Authentication:Authority"] = string.Empty,
        };

        // Set both ways: which one the host reads first differs between hosting models.
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
    }

    /// <summary>Stores a new API key with these scopes and returns it in plain text, as the one call that creates a key would.</summary>
    public async Task<(string PlainKey, ApiKey Key)> CreateKeyAsync(params string[] scopes) =>
        await CreateKeyAsync(expiresAt: null, scopes);

    public async Task<(string PlainKey, ApiKey Key)> CreateKeyAsync(DateTimeOffset? expiresAt, params string[] scopes)
    {
        using var scope = Services.CreateScope();
        var (key, plain) = ApiKey.Generate("tests", "Integration test", scopes, expiresAt, DateTimeOffset.UtcNow);
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
