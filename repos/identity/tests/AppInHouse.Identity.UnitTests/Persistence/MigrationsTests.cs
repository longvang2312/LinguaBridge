using AppInHouse.Identity.Infrastructure.Persistence;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace AppInHouse.Identity.UnitTests.Persistence;

/// <summary>
/// The migrations are checked without a database: the SQL Server provider is configured (so the real relational model and the
/// real SQL generator are used) but no connection is ever opened.
/// </summary>
public class MigrationsTests
{
    private const string FakeConnection = "Server=(none);Database=NotUsed;Trusted_Connection=True;TrustServerCertificate=True";

    private static TContext Create<TContext>(Func<DbContextOptions<TContext>, TContext> factory) where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        builder.UseIdentitySqlServer<TContext>(FakeConnection);
        return factory(builder.Options);
    }

    private static AppIdentityDbContext Identity() => Create<AppIdentityDbContext>(o => new AppIdentityDbContext(o));

    private static AppAuthDbContext Auth() => Create<AppAuthDbContext>(o => new AppAuthDbContext(o));

    private static AppAuditDbContext Audit() => Create<AppAuditDbContext>(o => new AppAuditDbContext(o));

    public static TheoryData<string> Contexts => new() { "identity", "auth", "audit" };

    private static DbContext For(string name) => name switch
    {
        "identity" => Identity(),
        "auth" => Auth(),
        _ => Audit(),
    };

    [Theory]
    [MemberData(nameof(Contexts))]
    public void EachContext_HasMigrations(string name)
    {
        using var context = For(name);

        Assert.NotEmpty(context.GetService<IMigrationsAssembly>().Migrations);
    }

    /// <summary>Fails when an entity or a mapping changed without a new migration (the snapshot is out of date).</summary>
    [Theory]
    [MemberData(nameof(Contexts))]
    public void TheModelSnapshot_MatchesTheCurrentModel_SoNoMigrationIsMissing(string name)
    {
        using var context = For(name);

        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var snapshotModel = snapshot!.Model;
        if (snapshotModel is IMutableModel mutable)
            snapshotModel = mutable.FinalizeModel();
        snapshotModel = context.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel);

        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(differences.Count == 0, "Pending model changes: " + string.Join(", ", differences.Select(d => d.GetType().Name)));
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void EachContext_KeepsItsOwnMigrationsHistoryTable(string name)
    {
        using var context = For(name);

        var script = context.GetService<IMigrator>().GenerateScript();

        Assert.Contains($"[__EFMigrationsHistory_{context.GetType().Name}]", script);
        Assert.DoesNotContain("[__EFMigrationsHistory]", script);
    }

    [Fact]
    public void IdentityMigration_CreatesTheIdentityAndOpenIddictTables()
    {
        using var context = Identity();

        var script = context.GetService<IMigrator>().GenerateScript();

        foreach (var table in new[] { "AspNetUsers", "AspNetRoles", "AspNetUserRoles", "OpenIddictApplications", "OpenIddictAuthorizations", "OpenIddictScopes", "OpenIddictTokens" })
            Assert.Contains($"CREATE TABLE [{table}]", script);
    }

    [Fact]
    public void AuthMigration_CreatesTheRbacTables_ButNotTheUserTablesOfTheIdentityContext()
    {
        using var context = Auth();

        var script = context.GetService<IMigrator>().GenerateScript();

        foreach (var table in new[] { "Projects", "Permissions", "RolePermissions", "ProjectUserAssignments", "UserRoleProjectAssignments", "UserPermissionAssignments", "PermissionCacheVersions" })
            Assert.Contains($"CREATE TABLE [{table}]", script);

        Assert.DoesNotContain("CREATE TABLE [AspNetUsers]", script);
        Assert.DoesNotContain("CREATE TABLE [AspNetRoles]", script);
        Assert.Contains("REFERENCES [AspNetUsers]", script); // it still relates to them
    }

    [Fact]
    public void AuditMigration_CreatesTheAuditTable()
    {
        using var context = Audit();

        Assert.Contains("CREATE TABLE [AuditLogs]", context.GetService<IMigrator>().GenerateScript());
    }

    [Fact]
    public void TheMigrations_NeverDropAnything_WhenGoingUpFromNothing()
    {
        foreach (var context in new DbContext[] { Identity(), Auth(), Audit() })
        {
            using (context)
            {
                var migrations = context.GetService<IMigrationsAssembly>();
                foreach (var (_, type) in migrations.Migrations)
                {
                    var migration = migrations.CreateMigration(type, context.Database.ProviderName!);
                    Assert.DoesNotContain(migration.UpOperations, op => op is DropTableOperation or DropColumnOperation);
                }
            }
        }
    }
}
