using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>A host that reuses a metrics snapshot for five minutes, as production does for a minute.</summary>
public sealed class CachedMetricsApiFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["Jobs:MetricsCacheSeconds"] = "300" };
}

public class MetricsApiTests
{
    private const string Overview = "/api/v1/metrics/overview";
    private const string Health = "/api/v1/metrics/health-status";

    private static string StatusOf(JsonElement overview, string id) =>
        overview.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id).GetProperty("status").GetString()!;

    private static JsonElement Service(JsonElement overview, string id) =>
        overview.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);

    private static object Mail() => new { to = NewRecipient(), subject = "s", textBody = "t" };

    [Fact]
    public async Task ANewInstallation_HasEightServices_FourOfThemIdleUntilTheyAreConfigured()
    {
        using var factory = new ApiFactory();
        var admin = await AdminAsync(factory);

        var response = await admin.GetAsync(Overview);
        var overview = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["email", "chat", "storage", "projects", "notifications", "jobs", "webhooks", "monitoring"], overview.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(["Idle", "Idle", "Idle", "Healthy", "Healthy", "Healthy", "Idle", "Healthy"], new[] { "email", "chat", "storage", "projects", "notifications", "jobs", "webhooks", "monitoring" }.Select(id => StatusOf(overview, id)));
        Assert.Equal((8, 4, 0, 4), (overview.GetProperty("servicesTotal").GetInt32(), overview.GetProperty("servicesHealthy").GetInt32(), overview.GetProperty("servicesDegraded").GetInt32(), overview.GetProperty("servicesIdle").GetInt32()));
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("successRatePercent").ValueKind);   // nothing was sent, so there is no rate
        Assert.Equal((0L, 0, 4), (overview.GetProperty("storageBytes").GetInt64(), overview.GetProperty("fileCount").GetInt32(), overview.GetProperty("activeProjects").GetInt32()));
        Assert.Equal(1, overview.GetProperty("activeApiKeys").GetInt32());   // the key this test just made
        Assert.True(overview.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.Equal("5", Service(overview, "notifications").GetProperty("metrics").GetProperty("activeTemplates").GetString());
        Assert.Equal("No active email configuration.", Service(overview, "email").GetProperty("detail").GetString());
    }

    [Fact]
    public async Task OnceAChannelIsConfiguredAndUsed_ItIsHealthy_AndTheFiguresFollow()
    {
        using var factory = new ApiFactory();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, "default");
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/emails/send", Mail())).StatusCode);

        var overview = await ReadAsync(await admin.GetAsync(Overview));

        Assert.Equal("Healthy", StatusOf(overview, "email"));
        Assert.Equal("Idle", StatusOf(overview, "chat"));
        Assert.Equal((3, 0, 100.0), (overview.GetProperty("deliveriesSent24h").GetInt32(), overview.GetProperty("deliveriesFailed24h").GetInt32(), overview.GetProperty("successRatePercent").GetDouble()));
        Assert.True(overview.GetProperty("totalRequests24h").GetInt64() >= 3);
        var email = Service(overview, "email").GetProperty("metrics");
        Assert.Equal(("1", "3", "0"), (email.GetProperty("activeConfigurations").GetString(), email.GetProperty("sent24h").GetString(), email.GetProperty("failed24h").GetString()));
    }

    [Fact]
    public async Task AChannelWhoseProviderFails_BecomesDegraded_OnceMoreThanAFifthOfAtLeastFiveDeliveriesFailed()
    {
        using var factory = new ApiFactory();
        var admin = await AdminAsync(factory);
        await ActivateSendGridAsync(admin, "default");
        factory.SendGrid.Respond = _ => Accepted();
        for (var i = 0; i < 20; i++) await admin.PostAsJsonAsync("/api/v1/emails/send", Mail());
        factory.SendGrid.Respond = _ => ServerError();
        for (var i = 0; i < 5; i++) await admin.PostAsJsonAsync("/api/v1/emails/send", Mail());

        var atTheLimit = await ReadAsync(await admin.GetAsync(Overview));   // 5 of 25 is exactly a fifth: not more
        await admin.PostAsJsonAsync("/api/v1/emails/send", Mail());          // 6 of 26
        var over = await ReadAsync(await admin.GetAsync(Overview));
        var health = await ReadAsync(await admin.GetAsync(Health));

        Assert.Equal("Healthy", StatusOf(atTheLimit, "email"));
        Assert.Equal("Degraded", StatusOf(over, "email"));
        Assert.Equal((20, 6), (over.GetProperty("deliveriesSent24h").GetInt32(), over.GetProperty("deliveriesFailed24h").GetInt32()));
        Assert.Equal(76.9, over.GetProperty("successRatePercent").GetDouble());
        Assert.Equal(1, over.GetProperty("servicesDegraded").GetInt32());
        Assert.Equal("Degraded", health.GetProperty("overall").GetString());
        Assert.Equal("Degraded", health.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "email").GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheStorageFigures_FollowTheFilesStored()
    {
        using var factory = new ApiFactory();
        var admin = await AdminAsync(factory);
        await ActivateAsync(admin, "default", "Storage", "LocalDisk", new { rootPath = "uploads" });
        var png = TestFiles.Png();
        for (var i = 0; i < 2; i++)
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } }, "file", "a.png" } };
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/v1/storage/upload", form)).StatusCode);
        }

        var overview = await ReadAsync(await admin.GetAsync(Overview));

        Assert.Equal("Healthy", StatusOf(overview, "storage"));
        Assert.Equal((png.LongLength * 2, 2), (overview.GetProperty("storageBytes").GetInt64(), overview.GetProperty("fileCount").GetInt32()));
        Assert.Equal("2", Service(overview, "storage").GetProperty("metrics").GetProperty("files").GetString());
    }

    [Fact]
    public async Task AFailedJob_MakesTheJobServiceDegraded_UntilItIsRetried()
    {
        using var factory = new ApiFactory();
        var admin = await AdminAsync(factory);
        var job = BackgroundJobRecord.Create(JobQueues.Outbox, "event", "{}", 1, DateTimeOffset.UtcNow);
        job.MarkAttemptFailed("down", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, permanent: true);
        await factory.WithDbAsync(async db =>
        {
            db.BackgroundJobs.Add(job);
            return await db.SaveChangesAsync();
        });

        var overview = await ReadAsync(await admin.GetAsync(Overview));

        Assert.Equal("Degraded", StatusOf(overview, "jobs"));
        Assert.Equal(1, overview.GetProperty("failedJobs").GetInt32());
        Assert.Equal("1", Service(overview, "jobs").GetProperty("metrics").GetProperty("failed").GetString());
    }

    [Fact]
    public async Task OnlyThePlatform_ReadsTheMetrics()
    {
        using var factory = new ApiFactory();
        var project = await ProjectClientAsync(factory, NewProject(), ApiScopes.ConfigRead);

        Assert.Equal(HttpStatusCode.Forbidden, (await project.GetAsync(Overview)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await project.GetAsync(Health)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync(Overview)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientWithKey((await factory.CreateKeyAsync(ApiScopes.EmailSend)).PlainKey).GetAsync(Health)).StatusCode);
    }

    [Fact]
    public async Task TheHealthStatus_IsTheSameEightServices_WithAnOverallVerdict()
    {
        using var factory = new ApiFactory();
        var admin = await AdminAsync(factory);

        var health = await ReadAsync(await admin.GetAsync(Health));
        var overview = await ReadAsync(await admin.GetAsync(Overview));

        Assert.Equal("Healthy", health.GetProperty("overall").GetString());
        Assert.Equal(overview.GetProperty("services").EnumerateArray().Select(s => (s.GetProperty("id").GetString(), s.GetProperty("status").GetString())),
            health.GetProperty("services").EnumerateArray().Select(s => (s.GetProperty("id").GetString(), s.GetProperty("status").GetString())));
        Assert.All(health.GetProperty("services").EnumerateArray(), s => Assert.Contains(s.GetProperty("status").GetString(), new[] { "Healthy", "Degraded", "Idle" }));
        Assert.NotEqual(default, health.GetProperty("generatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ASnapshot_IsReusedWithinTheCacheTime_UntilTheAggregationTaskRefreshesIt()
    {
        using var factory = new CachedMetricsApiFactory();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, "default");
        var first = await ReadAsync(await admin.GetAsync(Overview));

        await admin.PostAsJsonAsync("/api/v1/emails/send", Mail());
        var cached = await ReadAsync(await admin.GetAsync(Overview));
        var task = await ReadAsync(await admin.PostAsync("/api/v1/jobs/schedules/metrics-aggregate/run", null));
        var refreshed = await ReadAsync(await admin.GetAsync(Overview));

        Assert.Equal(0, first.GetProperty("deliveriesSent24h").GetInt32());
        Assert.Equal(first.GetProperty("generatedAt").GetDateTimeOffset(), cached.GetProperty("generatedAt").GetDateTimeOffset());
        Assert.Equal(0, cached.GetProperty("deliveriesSent24h").GetInt32());   // the figure a dashboard sees is at most as old as the cache time
        Assert.StartsWith("Success:", task.GetProperty("lastStatus").GetString());
        Assert.Equal(1, refreshed.GetProperty("deliveriesSent24h").GetInt32());
    }
}
