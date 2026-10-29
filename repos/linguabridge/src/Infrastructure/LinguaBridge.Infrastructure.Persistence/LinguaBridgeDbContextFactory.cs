using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LinguaBridge.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>, so migrations can be added without building the API host or reading appsettings.
/// Adding a migration never connects; the connection string matters only for <c>dotnet ef database update</c>.
/// </summary>
public class LinguaBridgeDbContextFactory : IDesignTimeDbContextFactory<LinguaBridgeDbContext>
{
    public LinguaBridgeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LinguaBridgeDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=LinguaBridge_Design;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new LinguaBridgeDbContext(options);
    }
}
