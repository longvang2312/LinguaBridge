using FluentValidation;
using LinguaBridge.Application;
using LinguaBridge.Modules.AiTutor.Commands;
using LinguaBridge.Modules.AiTutor.Quota;
using LinguaBridge.Modules.AiTutor.Queries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinguaBridge.Modules.AiTutor;

/// <summary>Prompt orchestration across LLM providers and the per-user token ledger.</summary>
public static class AiTutorModule
{
    public const string Name = "AiTutor";

    /// <summary>
    /// Registers the module's use cases. The model (<c>ILlmProvider</c>) comes from <c>AddInfrastructureExternal</c>;
    /// storage, the plan lookup and the learner's level (<c>IAiTutorRepository</c>, <c>IUserEntitlementSource</c>,
    /// <c>ILearnerLevelSource</c>) from Persistence; the clock from External.
    /// </summary>
    public static IServiceCollection AddAiTutorModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModule(Name, "ai");

        // Read when first needed: configuration added by the host after this point is only merged at build time.
        services.TryAddSingleton(serviceProvider => AiTutorOptions.From(serviceProvider.GetRequiredService<IConfiguration>()));

        services.AddValidatorsFromAssembly(typeof(AiTutorModule).Assembly, ServiceLifetime.Singleton);

        services.AddScoped<AiQuotaPolicy>();
        services.AddScoped<CreateSessionHandler>();
        services.AddScoped<SendMessageHandler>();
        services.AddScoped<EndSessionHandler>();
        services.AddScoped<GetMessagesHandler>();
        services.AddScoped<GetQuotaHandler>();

        return services;
    }
}
