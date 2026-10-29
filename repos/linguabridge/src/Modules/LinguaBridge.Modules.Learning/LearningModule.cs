using FluentValidation;
using LinguaBridge.Application;
using LinguaBridge.Modules.Learning.Commands;
using LinguaBridge.Modules.Learning.Queries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.Modules.Learning;

/// <summary>Curriculum delivery, quizzes, learner progress and SM-2 spaced repetition.</summary>
public static class LearningModule
{
    public const string Name = "Learning";

    /// <summary>
    /// Registers the module's use cases and validators. Storage (<c>ILearningRepository</c>, <c>ILearningReadModel</c>)
    /// and the view of Content (<c>ILessonSource</c>) come from Persistence; the clock from <c>AddInfrastructureExternal</c>.
    /// </summary>
    public static IServiceCollection AddLearningModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModule(Name, "learning", "srs");

        services.AddValidatorsFromAssembly(typeof(LearningModule).Assembly, ServiceLifetime.Singleton);

        services.AddScoped<StartAttemptHandler>();
        services.AddScoped<SubmitResponseHandler>();
        services.AddScoped<CompleteAttemptHandler>();
        services.AddScoped<ReviewCardHandler>();

        services.AddScoped<GetQuizHandler>();
        services.AddScoped<GetDueCardsHandler>();
        services.AddScoped<GetCourseProgressHandler>();

        return services;
    }
}
