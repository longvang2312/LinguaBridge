using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Api.Seeding;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>TASK-035 minor #2: a download that arrives in byte ranges is logged once, not once per range.</summary>
public class RangeLoggingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];

    private async Task<(HttpClient Client, string Project, Guid Id)> UploadedPdfAsync()
    {
        var admin = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, [.. ApiScopes.Assignable])).PlainKey);
        var project = NewProject();
        var saved = await admin.PostAsJsonAsync("/api/v1/channel-configurations", new { projectCode = project, channelType = "Storage", providerType = "LocalDisk", name = "Storage", settings = new { rootPath = "r-" + project } });
        await admin.PostAsync($"/api/v1/channel-configurations/{(await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()}/activate", null);
        var client = factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, ApiScopes.StorageUpload, ApiScopes.StorageRead)).PlainKey);
        var upload = await client.PostAsync("/api/v1/storage/upload", new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Pdf(5000)), "file", "doc.pdf" } });
        return (client, project, (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("file").GetProperty("id").GetGuid());
    }

    private async Task<int> DownloadsAsync(string project) => await factory.WithDbAsync(db => db.MediaLogs.CountAsync(l => l.ProjectCode == project && l.Action == "Download"));

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Guid id, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/storage/files/{id}");
        configure?.Invoke(request);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task TheWholeFile_IsOneDownload()
    {
        var (client, project, id) = await UploadedPdfAsync();

        var response = await GetAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await DownloadsAsync(project));
    }

    [Fact]
    public async Task ARangeThatStartsAtByteZero_IsLogged_AndTheRangesThatFollowAreNot()
    {
        var (client, project, id) = await UploadedPdfAsync();

        var first = await GetAsync(client, id, r => r.Headers.Range = new RangeHeaderValue(0, 999));
        var second = await GetAsync(client, id, r => r.Headers.Range = new RangeHeaderValue(1000, 1999));
        var third = await GetAsync(client, id, r => r.Headers.Range = new RangeHeaderValue(2000, 2999));

        Assert.All(new[] { first, second, third }, r => Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode));
        Assert.Equal(1000, (await first.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(1, await DownloadsAsync(project));   // one download, not three
    }

    [Fact]
    public async Task ASuffixRange_AnOpenEndedRangeFromTheMiddle_AndASecondRangeFromZero_AreTreatedByWhereTheyStart()
    {
        var (client, project, id) = await UploadedPdfAsync();

        await GetAsync(client, id, r => r.Headers.Range = new RangeHeaderValue(null, 100));      // the last 100 bytes
        await GetAsync(client, id, r => r.Headers.Range = new RangeHeaderValue(2500, null));     // from the middle to the end
        Assert.Equal(0, await DownloadsAsync(project));

        await GetAsync(client, id, r => r.Headers.Range = new RangeHeaderValue(0, null));        // from byte 0 to the end: a download
        Assert.Equal(1, await DownloadsAsync(project));
    }

    [Fact]
    public async Task EachFreshRequestForTheWholeFile_IsItsOwnDownload()
    {
        var (client, project, id) = await UploadedPdfAsync();

        await GetAsync(client, id);
        await GetAsync(client, id);

        Assert.Equal(2, await DownloadsAsync(project));
    }

    [Fact]
    public async Task ARangeOfAFileThatIsGone_IsStillLoggedAsAFailure()
    {
        var (client, project, id) = await UploadedPdfAsync();
        foreach (var path in Directory.GetFiles(factory.StoragePath, "*", SearchOption.AllDirectories).Where(p => p.Contains(project) || p.Contains("r-" + project))) File.Delete(path);

        var response = await GetAsync(client, id, r => r.Headers.Range = new RangeHeaderValue(1000, 1999));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.MediaLogs.AnyAsync(l => l.FileId == id && l.Action == "Download" && l.Status == "Failed")));
    }

    [Fact]
    public async Task AThumbnailRange_NeverWritesADownloadLog()
    {
        var (client, project, _) = await UploadedPdfAsync();
        var png = await client.PostAsync("/api/v1/storage/upload", new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()), "file", "a.png" } });
        var id = (await png.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("file").GetProperty("id").GetGuid();

        await client.GetAsync($"/api/v1/storage/files/{id}/thumbnail");

        Assert.Equal(0, await DownloadsAsync(project));
    }
}

/// <summary>TASK-035 minor #3: storage has its own switch for private networks.</summary>
public class StoragePrivateNetworkTests
{
    private const string Settings = """{"bucket":"media-bucket","region":"us-east-1","serviceUrl":"http://10.0.0.5:9000","forcePathStyle":true,"accessKeyId":"test-access-key-id"}""";

    private static async Task<(JsonElement Body, int Calls)> TestS3Async(ApiFactory factory)
    {
        var admin = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite)).PlainKey);
        factory.S3.Calls.Clear();
        factory.S3.Respond = request => request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("list-type")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<?xml version=\"1.0\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>media-bucket</Name><KeyCount>0</KeyCount><MaxKeys>1</MaxKeys><IsTruncated>false</IsTruncated></ListBucketResult>") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x42]) };

        var response = await admin.PostAsJsonAsync("/api/v1/storage/test", new { providerType = "S3", settings = JsonDocument.Parse(Settings).RootElement, secret = "Typed-S3-Secret-Access-Key-0123456789" });
        return (JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone(), factory.S3.Calls.Count);
    }

    [Fact]
    public async Task WhenOnlyEmailMayReachPrivateNetworks_StorageStillMayNot()
    {
        using var factory = new EmailPrivateOnlyApiFactory();

        var (body, calls) = await TestS3Async(factory);

        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("private", body.GetProperty("message").GetString());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task WhenOnlyStorageMayReachPrivateNetworks_ItDoes_WithoutOpeningMailToThem()
    {
        using var factory = new StoragePrivateOnlyApiFactory();

        var (body, calls) = await TestS3Async(factory);

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task EvenWithTheSwitchOn_TheMetadataAddressIsNeverReached()
    {
        using var factory = new StoragePrivateOnlyApiFactory();
        var admin = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigWrite)).PlainKey);
        factory.S3.Calls.Clear();

        var response = await admin.PostAsJsonAsync("/api/v1/storage/test", new
        {
            providerType = "S3",
            settings = JsonDocument.Parse(Settings.Replace("10.0.0.5:9000", "169.254.169.254")).RootElement,
            secret = "Typed-S3-Secret-Access-Key-0123456789",
        });

        Assert.False(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(factory.S3.Calls);
    }
}

/// <summary>TASK-035 minor #5: an absolute rootPath is refused on save unless the host allows absolute paths.</summary>
public class AbsoluteRootPathApiTests
{
    private static async Task<HttpResponseMessage> SaveAsync(ApiFactory factory, string project, string rootPath)
    {
        var admin = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite)).PlainKey);
        return await admin.PostAsJsonAsync("/api/v1/channel-configurations", new { projectCode = project, channelType = "Storage", providerType = "LocalDisk", name = "Storage", settings = new { rootPath } });
    }

    [Theory]
    [InlineData("/var/lib/files")]
    [InlineData("C:\\data\\files")]
    public async Task ByDefault_AnAbsolutePath_Is400OnSave_AndNothingIsStored(string rootPath)
    {
        using var factory = new ApiFactory();
        var project = "p" + Guid.NewGuid().ToString("N")[..8];

        var response = await SaveAsync(factory, project, rootPath);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("absolute paths are not allowed", await response.Content.ReadAsStringAsync());
        Assert.False(await factory.WithDbAsync(db => db.ChannelConfigurations.AnyAsync(c => c.ProjectCode == project)));
    }

    [Fact]
    public async Task ARelativeFolder_IsFine()
    {
        using var factory = new ApiFactory();

        Assert.Equal(HttpStatusCode.Created, (await SaveAsync(factory, "p" + Guid.NewGuid().ToString("N")[..8], "uploads")).StatusCode);
    }

    [Fact]
    public async Task WhenTheHostAllowsAbsolutePaths_AnAbsoluteOneIsSaved()
    {
        using var factory = new AbsolutePathsApiFactory();

        Assert.Equal(HttpStatusCode.Created, (await SaveAsync(factory, "p" + Guid.NewGuid().ToString("N")[..8], Path.Combine(Path.GetTempPath(), "ss-abs-ok"))).StatusCode);
    }
}

/// <summary>TASK-033 finding #2, through the API.</summary>
public class SmtpPasswordApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Configs = "/api/v1/channel-configurations";

    private async Task<HttpClient> AdminAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigRead, ApiScopes.ConfigWrite)).PlainKey);

    private static object Body(string project, object settings, string? secret = null) =>
        new { projectCode = project, channelType = "Email", providerType = "Smtp", name = "Mail", settings, secret };

    [Fact]
    public async Task ChangingTheHost_WithoutANewPassword_Is400OnSecret_AndWithOne_Is201()
    {
        var admin = await AdminAsync();
        var project = "p" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(Configs, Body(project, new { host = "smtp.example.com", port = 587, username = "mailer" }, "Stored-P@ssw0rd-1"))).StatusCode);

        var refused = await admin.PostAsJsonAsync(Configs, Body(project, new { host = "smtp.evil.example", port = 587, username = "mailer" }));
        var accepted = await admin.PostAsJsonAsync(Configs, Body(project, new { host = "smtp.evil.example", port = 587, username = "mailer" }, "A-new-P@ssw0rd-2"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Secret", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task ChangingTheUsername_WithoutANewPassword_Is400_ButChangingThePortIsNot()
    {
        var admin = await AdminAsync();
        var project = "p" + Guid.NewGuid().ToString("N")[..8];
        await admin.PostAsJsonAsync(Configs, Body(project, new { host = "smtp.example.com", port = 587, username = "mailer" }, "Stored-P@ssw0rd-1"));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Configs, Body(project, new { host = "smtp.example.com", port = 587, username = "someone-else" }))).StatusCode);
        var portOnly = await admin.PostAsJsonAsync(Configs, Body(project, new { host = "smtp.example.com", port = 465, username = "mailer" }));
        Assert.Equal(HttpStatusCode.Created, portOnly.StatusCode);
        Assert.True((await portOnly.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasSecret").GetBoolean());
    }
}

/// <summary>TASK-034 finding #2, through the API.</summary>
public class WebhookInSettingsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AWebhookPastedIntoTheAvatarSetting_Is400_AndIsNotStored()
    {
        var admin = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, ApiScopes.ConfigWrite)).PlainKey);
        var project = "p" + Guid.NewGuid().ToString("N")[..8];
        var webhook = "https://discord.com/api/webhooks/" + "123456789012345678" + "/" + "fake-webhook-token-for-tests-0123456789";

        var response = await admin.PostAsJsonAsync("/api/v1/channel-configurations", new
        {
            projectCode = project, channelType = "Chat", providerType = "DiscordWebhook", name = "Chat", settings = new { avatarUrl = webhook }, secret = webhook,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("avatarUrl", text);
        Assert.DoesNotContain("fake-webhook-token", text);
        Assert.False(await factory.WithDbAsync(db => db.ChannelConfigurations.AnyAsync(c => c.ProjectCode == project)));
    }
}

/// <summary>The first key of a new deployment.</summary>
public class BootstrapKeyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static IConfiguration Config(string? key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SharedServices:Bootstrap:AdminApiKey"] = key }).Build();

    [Fact]
    public async Task OnAnEmptyDatabase_TheBootstrapKeyBecomesAPlatformKeyWithEveryScope_AndOnlyThen()
    {
        const string key = "aih_live_bootstrap" + "0123456789abcdefghijklmnopqrstuv";
        Assert.False(await factory.WithDbAsync(db => db.ApiKeys.AnyAsync()));   // a fresh host: nothing yet

        await BootstrapKey.EnsureAsync(factory.Services, Config(key));

        var row = await factory.WithDbAsync(db => db.ApiKeys.AsNoTracking().SingleAsync());
        Assert.Equal(("default", key[..15], ApiKey.Hash(key)), (row.ProjectCode, row.KeyPrefix, row.HashedKey));
        Assert.Equal(ApiScopes.Assignable.Order(), row.ScopeList.Order());
        Assert.Null(row.ExpiresAt);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithKey(key).GetAsync("/api/v1/api-keys")).StatusCode);   // it works as a platform key
        Assert.Equal(HttpStatusCode.Created, (await factory.ClientWithKey(key).PostAsJsonAsync("/api/v1/api-keys", new { projectCode = "identity", name = "first real key", scopes = new[] { "email:send" } })).StatusCode);

        // A second start, even with a different key in the setting, adds nothing: the database has keys now.
        await BootstrapKey.EnsureAsync(factory.Services, Config("aih_live_another" + "0123456789abcdefghijklmnopqrstuvwx"));
        Assert.Equal(2, await factory.WithDbAsync(db => db.ApiKeys.CountAsync()));   // the bootstrap key and the one it made
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey("aih_live_another" + "0123456789abcdefghijklmnopqrstuvwx").GetAsync("/api/v1/api-keys")).StatusCode);
    }

    [Fact]
    public async Task NoSetting_MeansNothingHappens()
    {
        var before = await factory.WithDbAsync(db => db.ApiKeys.CountAsync());

        await BootstrapKey.EnsureAsync(factory.Services, Config(null));
        await BootstrapKey.EnsureAsync(factory.Services, Config("  "));

        Assert.Equal(before, await factory.WithDbAsync(db => db.ApiKeys.CountAsync()));
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("aih_live_tooshort")]
    [InlineData("sk_" + "live_0123456789abcdefghijklmnopqrstuvwxyz")]   // split so secret scanners do not mistake the test value for a real key
    public async Task AKeyOfTheWrongShape_StopsTheStart_InsteadOfBeingIgnored(string key)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BootstrapKey.EnsureAsync(factory.Services, Config(key)));

        Assert.Contains("aih_live_", error.Message);
        Assert.DoesNotContain(key, error.Message);   // the message never repeats what was given
    }
}
