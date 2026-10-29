using LinguaBridge.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.Modules.Content;

/// <summary>Course builder, question bank, localization and lesson lifecycle (Draft, Published, Archived).</summary>
public static class ContentModule
{
    public const string Name = "Content";

    public static IServiceCollection AddContentModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddModule(Name, "content");
        return services;
    }
}
