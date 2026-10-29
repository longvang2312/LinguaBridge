using LinguaBridge.Infrastructure.Persistence.AiTutor;
using LinguaBridge.Infrastructure.Persistence.Content;
using LinguaBridge.Infrastructure.Persistence.Learning;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.Content.Abstractions;
using LinguaBridge.Modules.Learning.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinguaBridge.Infrastructure.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Registers <see cref="LinguaBridgeDbContext"/> on PostgreSQL plus the storage behind each module's
    /// repository and read-model interfaces. The connection is opened lazily, so the host starts without a
    /// database; the first query fails if <c>ConnectionStrings:LinguaBridgeDb</c> is not set.
    /// </summary>
    public static IServiceCollection AddInfrastructurePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Read when first needed, not now: configuration added by the host after this point (tests, containers)
        // is only merged into the final IConfiguration at build time.
        services.TryAddSingleton(serviceProvider => new PersistenceOptions
        {
            ConnectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString(PersistenceOptions.ConnectionStringName),
        });

        services.AddDbContext<LinguaBridgeDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<PersistenceOptions>().ConnectionString));

        services.AddScoped<IContentRepository, ContentRepository>();
        services.AddScoped<IContentReadModel, ContentReadModel>();

        services.AddScoped<ILearningRepository, LearningRepository>();
        services.AddScoped<ILessonSource, LessonSource>();
        services.AddScoped<ILearningReadModel, LearningReadModel>();

        services.AddScoped<IAiTutorRepository, AiTutorRepository>();
        services.AddScoped<IUserEntitlementSource, UserEntitlementSource>();
        services.AddScoped<ILearnerLevelSource, LearnerLevelSource>();

        return services;
    }
}
