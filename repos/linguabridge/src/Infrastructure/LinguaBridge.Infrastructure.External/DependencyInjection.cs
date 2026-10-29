using LinguaBridge.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinguaBridge.Infrastructure.External;

public static class DependencyInjection
{
    /// <summary>Clients for services outside the process (LLM providers, object storage, payment gateways) go here.</summary>
    public static IServiceCollection AddInfrastructureExternal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        return services;
    }
}
