using LinguaBridge.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.Modules.Billing;

/// <summary>Plans, subscriptions, entitlements and the idempotent payment webhook inbox.</summary>
public static class BillingModule
{
    public const string Name = "Billing";

    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModule(Name, "billing");
        return services;
    }
}
