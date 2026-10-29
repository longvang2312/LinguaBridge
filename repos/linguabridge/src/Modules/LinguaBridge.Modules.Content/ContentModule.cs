using FluentValidation;
using LinguaBridge.Application;
using LinguaBridge.Modules.Content.Commands;
using LinguaBridge.Modules.Content.Queries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.Modules.Content;

/// <summary>Course builder, question bank, localization and lesson lifecycle (Draft, Published, Archived).</summary>
public static class ContentModule
{
    public const string Name = "Content";

    /// <summary>
    /// Registers the module's use cases and validators. The storage behind <c>IContentRepository</c> and
    /// <c>IContentReadModel</c> comes from Persistence (<c>AddInfrastructurePersistence</c>).
    /// </summary>
    public static IServiceCollection AddContentModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModule(Name, "content");

        services.AddValidatorsFromAssembly(typeof(ContentModule).Assembly, ServiceLifetime.Singleton);

        services.AddScoped<CreateCourseHandler>();
        services.AddScoped<UpdateCourseHandler>();
        services.AddScoped<PublishCourseHandler>();
        services.AddScoped<ArchiveCourseHandler>();
        services.AddScoped<AddCourseTranslationHandler>();
        services.AddScoped<UpdateCourseTranslationHandler>();
        services.AddScoped<CreateLessonHandler>();
        services.AddScoped<PublishLessonHandler>();
        services.AddScoped<AddLessonTranslationHandler>();
        services.AddScoped<AddQuestionHandler>();
        services.AddScoped<AddQuestionOptionHandler>();

        services.AddScoped<GetPublishedCoursesHandler>();
        services.AddScoped<GetAllCoursesHandler>();
        services.AddScoped<GetCourseDetailHandler>();
        services.AddScoped<GetLessonsHandler>();
        services.AddScoped<PreviewLessonHandler>();

        return services;
    }
}
