using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AppInHouse.Identity.Infrastructure.Persistence;

/// <summary>
/// Used only by the EF Core tools (<c>dotnet ef migrations add</c>, <c>dotnet ef database update</c>). The connection
/// string comes from the <c>ConnectionStrings__IdentityDb</c> environment variable, falling back to LocalDB.
/// </summary>
internal static class DesignTimeConnection
{
    private const string Fallback = "Server=(localdb)\\MSSQLLocalDB;Database=AppInHouse.Identity;Trusted_Connection=True;TrustServerCertificate=True";

    public static DbContextOptions<TContext> Options<TContext>() where TContext : DbContext
    {
        var connectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{SqlServerOptionsExtensions.ConnectionStringName}");
        var builder = new DbContextOptionsBuilder<TContext>();
        builder.UseIdentitySqlServer<TContext>(string.IsNullOrWhiteSpace(connectionString) ? Fallback : connectionString);
        return builder.Options;
    }
}

internal sealed class AppIdentityDbContextFactory : IDesignTimeDbContextFactory<AppIdentityDbContext>
{
    public AppIdentityDbContext CreateDbContext(string[] args) => new(DesignTimeConnection.Options<AppIdentityDbContext>());
}

internal sealed class AppAuthDbContextFactory : IDesignTimeDbContextFactory<AppAuthDbContext>
{
    public AppAuthDbContext CreateDbContext(string[] args) => new(DesignTimeConnection.Options<AppAuthDbContext>());
}

internal sealed class AppAuditDbContextFactory : IDesignTimeDbContextFactory<AppAuditDbContext>
{
    public AppAuditDbContext CreateDbContext(string[] args) => new(DesignTimeConnection.Options<AppAuditDbContext>());
}
