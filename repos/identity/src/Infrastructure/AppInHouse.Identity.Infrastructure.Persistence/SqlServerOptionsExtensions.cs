using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence;

public static class SqlServerOptionsExtensions
{
    /// <summary>The connection string name shared by every host of the identity platform.</summary>
    public const string ConnectionStringName = "IdentityDb";

    /// <summary>
    /// Configures SQL Server for one of the identity DbContexts. The three contexts live in one database, so each keeps its
    /// own migrations history table (<c>__EFMigrationsHistory_{Context}</c>); with a shared table they would see each
    /// other's migrations as unknown.
    /// </summary>
    public static DbContextOptionsBuilder UseIdentitySqlServer<TContext>(this DbContextOptionsBuilder builder, string? connectionString)
        where TContext : DbContext
        => builder.UseSqlServer(
            connectionString,
            sql => sql.MigrationsHistoryTable($"__EFMigrationsHistory_{typeof(TContext).Name}"));
}
