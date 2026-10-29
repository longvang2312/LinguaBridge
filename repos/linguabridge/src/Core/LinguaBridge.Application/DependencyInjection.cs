using LinguaBridge.Application.Messaging;
using LinguaBridge.Application.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinguaBridge.Application;

public static class DependencyInjection
{
    /// <summary>Services shared by every module: the in-process event bus.</summary>
    public static IServiceCollection AddApplicationCore(this IServiceCollection services)
    {
        services.TryAddScoped<IEventBus, InProcessEventBus>();
        return services;
    }

    /// <summary>
    /// Called by each module's <c>AddXxxModule</c>. Also pulls in <see cref="AddApplicationCore"/>, so the order
    /// in which modules are added does not matter. Adding the same module twice registers it once.
    /// </summary>
    public static IServiceCollection AddModule(this IServiceCollection services, string name, params string[] schemas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.AddApplicationCore();

        var alreadyAdded = services.Any(d =>
            d.ServiceType == typeof(IModuleDescriptor) && d.ImplementationInstance is IModuleDescriptor m && m.Name == name);
        if (!alreadyAdded)
            services.AddSingleton<IModuleDescriptor>(new ModuleDescriptor(name, schemas));

        return services;
    }
}
