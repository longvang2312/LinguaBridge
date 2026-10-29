using LinguaBridge.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.Modules.Learning;

/// <summary>Curriculum delivery, quizzes, learner progress and SM-2 spaced repetition.</summary>
public static class LearningModule
{
    public const string Name = "Learning";

    public static IServiceCollection AddLearningModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModule(Name, "learning", "srs");
        return services;
    }
}
