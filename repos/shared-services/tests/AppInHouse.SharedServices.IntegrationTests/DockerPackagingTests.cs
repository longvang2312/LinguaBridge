using System.Text.RegularExpressions;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>
/// The deployment files are code too: these tests read them and check what a broken deployment would get wrong first (a missing volume
/// loses every stored secret, a wrong health-check port keeps a container "unhealthy", a password written into the file ends up in git).
/// They do not need Docker. Starting the stack for real is done by hand (see the task report).
/// </summary>
public class DockerPackagingTests
{
    private static string Find(string fileName, string? folderContains = null)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, fileName);
            if (folderContains is null && File.Exists(direct)) return direct;
            if (folderContains is not null && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                var found = Directory.EnumerateFiles(directory.FullName, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault(p => p.Contains(folderContains) && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
                if (found is not null) return found;
            }
        }

        throw new FileNotFoundException($"{fileName} was not found above {AppContext.BaseDirectory}");
    }

    private static string Compose => File.ReadAllText(Find("docker-compose.shared-services.yml"));
    private static string ApiDockerfile => File.ReadAllText(Find("Dockerfile", "AppInHouse.SharedServices.Api"));
    private static string WebDockerfile => File.ReadAllText(Find("Dockerfile", "AppInHouse.SharedServices.Web"));

    /// <summary>The text of one service of the compose file (from its name to the next service or the volumes section).</summary>
    private static string Service(string name)
    {
        var match = Regex.Match(Compose, $@"(?ms)^  {Regex.Escape(name)}:\r?\n(.*?)(?=^  \S|^\S|\z)");
        Assert.True(match.Success, $"service {name} is not in the compose file");
        return match.Groups[1].Value;
    }

    [Fact]
    public void TheComposeFile_HasTheDatabase_TheApiAndThePortal()
    {
        foreach (var service in new[] { "shared-services-db", "shared-services-api", "shared-services-web" }) Assert.NotEmpty(Service(service));
        Assert.Contains("image: postgres:", Service("shared-services-db"));
    }

    [Fact]
    public void TheApi_ListensOn5030_ThePortalOn5031_AndBothPortsCanBeMoved()
    {
        Assert.Contains("\"${SHARED_SERVICES_API_PORT:-5030}:5030\"", Service("shared-services-api"));
        Assert.Contains("\"${SHARED_SERVICES_WEB_PORT:-5031}:5031\"", Service("shared-services-web"));
        Assert.Contains("ASPNETCORE_URLS: http://+:5030", Service("shared-services-api"));
        Assert.Contains("ASPNETCORE_URLS: http://+:5031", Service("shared-services-web"));
    }

    [Fact]
    public void TheDatabase_IsNotPublished_AndKeepsItsDataInAVolume()
    {
        var db = Service("shared-services-db");

        Assert.DoesNotContain("ports:", db);   // only the API reaches it, over the compose network
        Assert.Matches(@"shared-services-pgdata:/var/lib/postgresql/data", db);
        Assert.Contains("pg_isready", db);
    }

    [Fact]
    public void TheApi_KeepsTheKeyRingAndTheFilesInVolumes_AtThePathsTheTaskNames()
    {
        var api = Service("shared-services-api");

        Assert.Contains("shared-services-dp-keys:/root/.aspnet/DataProtection-Keys", api);
        Assert.Contains("shared-services-storage:/data/storage", api);
        Assert.Contains("DataProtection__KeysPath: /root/.aspnet/DataProtection-Keys", api);
        Assert.Contains("Storage__BasePath: /data/storage", api);
        foreach (var volume in new[] { "shared-services-pgdata:", "shared-services-dp-keys:", "shared-services-storage:", "shared-services-web-dp-keys:" })
            Assert.Matches($@"(?m)^volumes:(?:\r?\n  \S.*)*\r?\n  {Regex.Escape(volume)}", Compose);   // each is declared, so it is a named volume that survives `down`
    }

    [Fact]
    public void TheApi_UsesPostgres_AndMigratesOnStart_SoAHealthyApiMeansAReadyDatabase()
    {
        var api = Service("shared-services-api");

        Assert.Contains("Database__Provider: Postgres", api);
        Assert.Contains("Database__MigrateOnStartup: \"true\"", api);
        Assert.Contains("Host=shared-services-db", api);
        Assert.Matches(@"(?s)depends_on:\s+shared-services-db:\s+condition: service_healthy", api);
    }

    [Theory]
    [InlineData("shared-services-api", 5030)]
    [InlineData("shared-services-web", 5031)]
    public void EachService_HasAHealthCheckOnItsOwnPort(string service, int port)
    {
        var text = Service(service);

        Assert.Matches($@"(?s)healthcheck:.*?""http://localhost:{port}/health""", text);
        Assert.Contains("start_period", text);
    }

    [Fact]
    public void ThePortal_WaitsForAHealthyApi_AndTalksToItByTheComposeName()
    {
        var web = Service("shared-services-web");

        Assert.Matches(@"(?s)depends_on:\s+shared-services-api:\s+condition: service_healthy", web);
        Assert.Contains("SharedServices__Api__BaseUrl: http://shared-services-api:5030", web);
    }

    [Fact]
    public void NoPasswordOrKeyIsWrittenIntoTheFile_TheyComeFromTheEnvironment_AndStartingWithoutThemFails()
    {
        var compose = Compose;

        Assert.Contains("${SHARED_SERVICES_DB_PASSWORD:?", compose);
        Assert.Contains("${SHARED_SERVICES_ADMIN_KEY:?", compose);
        Assert.DoesNotMatch(@"aih_live_[A-Za-z0-9_-]{10,}", Regex.Replace(compose, @"(?m)^\s*#.*$", string.Empty));   // not in the settings (comments only show the shape)
        Assert.DoesNotMatch(@"POSTGRES_PASSWORD:\s+(?!\$\{)\S", compose);
        Assert.DoesNotMatch(@"Password=(?!\$\{)[^;\s]", compose);
    }

    [Fact]
    public void TheApi_IsProductionNotDevelopment_SoNoSeededKeyAndNoSwagger()
    {
        foreach (var service in new[] { "shared-services-api", "shared-services-web" })
            Assert.Contains("ASPNETCORE_ENVIRONMENT: Production", Service(service));
        Assert.DoesNotContain("Seeding", Compose);
    }

    [Fact]
    public void TheFirstKey_IsGivenToTheApiAsTheBootstrapKey_AndToThePortalAsItsKey()
    {
        Assert.Contains("SharedServices__Bootstrap__AdminApiKey: ${SHARED_SERVICES_ADMIN_KEY:?", Service("shared-services-api"));
        Assert.Contains("SharedServices__Api__ApiKey: ${SHARED_SERVICES_ADMIN_KEY:?", Service("shared-services-web"));
    }

    [Fact]
    public void BothServices_AreBuiltFromTheSolutionsFolder_WithTheirOwnDockerfile()
    {
        Assert.Matches(@"(?s)context: \./repos/shared-services\s+dockerfile: src/Presentation/AppInHouse\.SharedServices\.Api/Dockerfile", Service("shared-services-api"));
        Assert.Matches(@"(?s)context: \./repos/shared-services\s+dockerfile: src/Presentation/AppInHouse\.SharedServices\.Web/Dockerfile", Service("shared-services-web"));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Find("Directory.Build.props"))!, "src", "Presentation", "AppInHouse.SharedServices.Api", "Dockerfile")));
    }

    [Theory]
    [InlineData("Api", 5030)]
    [InlineData("Web", 5031)]
    public void EachDockerfile_BuildsInTwoStages_ExposesItsPort_HasAHealthCheck_AndRunsTheRightDll(string which, int port)
    {
        var text = which == "Api" ? ApiDockerfile : WebDockerfile;

        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build", text);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final", text);
        Assert.Contains($"EXPOSE {port}", text);
        Assert.Contains($"ASPNETCORE_URLS=http://+:{port}", text);
        Assert.Matches($@"(?s)HEALTHCHECK .*curl --fail --silent http://localhost:{port}/health", text);
        Assert.Contains($"ENTRYPOINT [\"dotnet\", \"AppInHouse.SharedServices.{which}.dll\"]", text);
        Assert.Contains("apt-get install -y --no-install-recommends curl", text);   // the health check needs it and the base image does not have it
        Assert.Matches(@"(?s)dotnet restore .*COPY src/ src/.*dotnet publish", text);   // restore before the sources, so the packages are cached
        Assert.DoesNotContain("SDK_PREVIEW", text);
        Assert.DoesNotMatch(@"10\.0-preview", text);
    }

    [Fact]
    public void TheApiDockerfile_DeclaresTheTwoVolumes_AndPointsTheSettingsAtThem()
    {
        Assert.Contains("VOLUME [\"/root/.aspnet/DataProtection-Keys\", \"/data/storage\"]", ApiDockerfile);
        Assert.Contains("DataProtection__KeysPath=/root/.aspnet/DataProtection-Keys", ApiDockerfile);
        Assert.Contains("Storage__BasePath=/data/storage", ApiDockerfile);
        Assert.Contains("VOLUME [\"/root/.aspnet/DataProtection-Keys\"]", WebDockerfile);
    }

    [Fact]
    public void ThePortalDockerfile_OnlyCopiesTheSdk_NotTheServicesOwnProjects()
    {
        Assert.Contains("AppInHouse.SharedServices.ClientSdk", WebDockerfile);
        Assert.DoesNotContain("SharedServices.Domain", WebDockerfile);
        Assert.DoesNotContain("SharedServices.Persistence", WebDockerfile);
    }

    [Fact]
    public void TheDockerIgnore_KeepsBuildOutputLocalDataAndSecretsOutOfTheImage()
    {
        var text = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Find("Directory.Build.props"))!, ".dockerignore"));

        foreach (var pattern in new[] { "**/bin/", "**/obj/", "**/data/", "**/*.db" }) Assert.Contains(pattern, text);
    }
}
