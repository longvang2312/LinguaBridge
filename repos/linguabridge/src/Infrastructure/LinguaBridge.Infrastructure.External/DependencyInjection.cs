using LinguaBridge.Application.Abstractions;
using LinguaBridge.Infrastructure.External.Llm;
using LinguaBridge.Modules.AiTutor.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LinguaBridge.Infrastructure.External;

public static class DependencyInjection
{
    /// <summary>
    /// Clients for services outside the process (LLM providers, object storage, payment gateways) go here.
    /// The LLM is OpenAI, with Azure OpenAI as a fallback when it is configured. Neither key is required to start the
    /// host: without a key the first AI call fails with a provider error.
    /// </summary>
    public static IServiceCollection AddInfrastructureExternal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        // Options are read when first needed: configuration added by the host after this point (tests, containers)
        // is only merged into the final IConfiguration at build time.
        services.TryAddSingleton(serviceProvider => OpenAiOptions.From(serviceProvider.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(serviceProvider => AzureOpenAiOptions.From(serviceProvider.GetRequiredService<IConfiguration>()));

        services.AddHttpClient<OpenAiLlmProvider>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<OpenAiOptions>();
            client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddHttpClient<AzureOpenAiLlmProvider>((serviceProvider, client) =>
            client.Timeout = TimeSpan.FromSeconds(serviceProvider.GetRequiredService<AzureOpenAiOptions>().TimeoutSeconds));

        services.TryAddTransient<ILlmProvider>(serviceProvider =>
        {
            var openAi = serviceProvider.GetRequiredService<OpenAiLlmProvider>();
            return serviceProvider.GetRequiredService<AzureOpenAiOptions>().IsConfigured
                ? new FallbackLlmProvider(openAi, serviceProvider.GetRequiredService<AzureOpenAiLlmProvider>(), serviceProvider.GetRequiredService<ILogger<FallbackLlmProvider>>())
                : openAi;
        });

        return services;
    }
}
