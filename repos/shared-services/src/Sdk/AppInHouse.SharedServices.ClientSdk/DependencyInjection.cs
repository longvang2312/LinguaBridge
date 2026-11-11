using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AppInHouse.SharedServices.ClientSdk;

public sealed class SharedServicesClientOptions
{
    public const string SectionName = "SharedServices:Api";

    /// <summary>For example <c>http://localhost:5030</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5030";

    /// <summary>Sent as the <c>X-Api-Key</c> header. Keep it in configuration or a secret store, never in code.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

public static class DependencyInjection
{
    public const string ApiKeyHeader = "X-Api-Key";

    public static IServiceCollection AddSharedServicesClient(this IServiceCollection services, Action<SharedServicesClientOptions> configure)
    {
        services.Configure(configure);
        services.AddHttpClient<ISharedServicesClient, SharedServicesClient>((provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<SharedServicesClientOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(options.ApiKey)) http.DefaultRequestHeaders.Add(ApiKeyHeader, options.ApiKey);
        });
        return services;
    }
}
