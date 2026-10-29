using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinguaBridge.Infrastructure.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Registers <see cref="LinguaBridgeDbContext"/> on PostgreSQL. The connection is opened lazily, so the host
    /// starts without a database; the first query fails if <c>ConnectionStrings:LinguaBridgeDb</c> is not set.
    /// </summary>
    public static IServiceCollection AddInfrastructurePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(new PersistenceOptions
        {
            ConnectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName),
        });

        services.AddDbContext<LinguaBridgeDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<PersistenceOptions>().ConnectionString));

        return services;
    }
}
