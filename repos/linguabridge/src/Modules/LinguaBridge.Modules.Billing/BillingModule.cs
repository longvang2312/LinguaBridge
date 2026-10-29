using FluentValidation;
using LinguaBridge.Application;
using LinguaBridge.Modules.Billing.Commands;
using LinguaBridge.Modules.Billing.Queries;
using LinguaBridge.Modules.Billing.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinguaBridge.Modules.Billing;

/// <summary>Plans, subscriptions, entitlements and the idempotent payment webhook inbox.</summary>
public static class BillingModule
{
    public const string Name = "Billing";

    /// <summary>
    /// Registers the module's use cases and the VNPay and Stripe webhook providers. Storage (<c>IBillingRepository</c>)
    /// comes from Persistence and the clock from <c>AddInfrastructureExternal</c>.
    /// </summary>
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModule(Name, "billing");

        // Read when first needed: configuration added by the host after this point is only merged at build time.
        services.TryAddSingleton(serviceProvider => WebhookOptions.From(serviceProvider.GetRequiredService<IConfiguration>()));

        services.AddValidatorsFromAssembly(typeof(BillingModule).Assembly, ServiceLifetime.Singleton);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebhookProvider, VnPayWebhookProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebhookProvider, StripeWebhookProvider>());

        services.AddScoped<CreatePlanHandler>();
        services.AddScoped<GetPlansHandler>();
        services.AddScoped<GetEntitlementHandler>();
        services.AddScoped<ProcessWebhookHandler>();

        return services;
    }
}
