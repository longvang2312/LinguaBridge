using AppInHouse.SharedServices.Application.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppInHouse.SharedServices.Application;

public static class DependencyInjection
{
    /// <summary>The use cases. Persistence, the secret protector and the actor come from the other layers and the host.</summary>
    public static IServiceCollection AddSharedServicesApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IChannelConfigManager, ChannelConfigManager>();
        return services;
    }
}
