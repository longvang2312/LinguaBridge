using LinguaBridge.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.Modules.AiTutor;

/// <summary>Prompt orchestration across LLM providers and the per-user token ledger.</summary>
public static class AiTutorModule
{
    public const string Name = "AiTutor";

    public static IServiceCollection AddAiTutorModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModule(Name, "ai");
        return services;
    }
}
