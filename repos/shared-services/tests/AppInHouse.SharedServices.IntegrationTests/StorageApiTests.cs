using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.IntegrationTests;

public class StorageApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Configs = "/api/v1/channel-configurations";
    private const string Upload = "/api/v1/storage/upload";
    private const string Files = "/api/v1/storage/files";
    private const string Test = "/api/v1/storage/test";
    private const string MediaLogs = "/api/v1/storage/media-logs";
    private const string S3Secret = "Typed-S3-Secret-Access-Key-0123456789";
    private const string StoredS3Secret = "Stored-S3-Secret-Access-Key-9876543210";

    private static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpClient> AdminAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null,
            ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete)).PlainKey);

    private async Task<HttpClient> ProjectClientAsync(string project, params string[] scopes) =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, scopes)).PlainKey);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    /// <summary>Saves a storage configuration through the API and (by default) activates it.</summary>
    private static async Task<JsonElement> SaveAsync(HttpClient admin, string project, string provider, object settings, string? secret = null, bool activate = true, string name = "Storage")
    {
        var response = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Storage", providerType = provider, name, settings, secret });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await ReadAsync(response);
        if (activate) Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Configs}/{saved.GetProperty("id").GetGuid()}/activate", null)).StatusCode);
        return saved;
    }

    /// <summary>A project with an active local-disk storage in its own folder. Returns the folder name (under the host's storage base).</summary>
    private async Task<string> LocalProjectAsync(HttpClient admin, string project, int maxMb = 5)
    {
        var root = "root-" + Guid.NewGuid().ToString("N")[..8];
        await SaveAsync(admin, project, "LocalDisk", new { rootPath = root, maxFileSizeMb = maxMb });
        return root;
    }

    private static MultipartFormDataContent Form(byte[] bytes, string fileName, string? contentType = "image/png", bool? isPublic = null, string? project = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        if (contentType is not null) file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        if (isPublic is { } pub) form.Add(new StringContent(pub ? "true" : "false"), "isPublic");
        if (project is not null) form.Add(new StringContent(project), "projectCode");
        return form;
    }

    private string DiskPath(string root, string objectKey) => Path.Combine(factory.StoragePath, root, objectKey.Replace('/', Path.DirectorySeparatorChar));

    private static string[] FilesUnder(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];

    private async Task<(JsonElement File, string ObjectKey)> UploadPngAsync(HttpClient client, string project, string root, string name = "photo.png", bool? isPublic = null)
    {
        var response = await client.PostAsync(Upload, Form(TestFiles.Png(), name, "image/png", isPublic));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        var file = body.GetProperty("file");
        var key = FilesUnder(Path.Combine(factory.StoragePath, root)).Select(p => p.Replace(Path.DirectorySeparatorChar, '/'))
            .Single(p => p.EndsWith(file.GetProperty("id").GetGuid().ToString("N") + ".png", StringComparison.Ordinal));
        return (file, key[(key.IndexOf("/uploads/", StringComparison.Ordinal) + 1)..]);
    }

    // ------------------------------------------------------------------------------------------------ upload

    [Fact]
    public async Task APngUpload_IsStoredUnderAServerMadeKey_WithAThumbnailNextToIt_AndAnswersWithTheFileAndItsUrls()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);
        var png = TestFiles.Png();

        var response = await client.PostAsync(Upload, Form(png, "Holiday.png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        var file = body.GetProperty("file");
        var id = file.GetProperty("id").GetGuid();
        Assert.Equal((project, "image/png", "Holiday.png", png.Length, false, true), (file.GetProperty("projectCode").GetString(), file.GetProperty("mimeType").GetString(),
            file.GetProperty("originalFileName").GetString(), file.GetProperty("fileSizeBytes").GetInt32(), file.GetProperty("isPublic").GetBoolean(), file.GetProperty("hasThumbnail").GetBoolean()));
        Assert.Equal($"/api/v1/storage/files/{id}", body.GetProperty("url").GetString());
        Assert.Equal($"/api/v1/storage/files/{id}/thumbnail", body.GetProperty("thumbnailUrl").GetString());
        Assert.Equal($"/api/v1/storage/files/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(png).Select(b => b.ToString("x2")).Aggregate(string.Concat), file.GetProperty("fileHashSha256").GetString());
        Assert.DoesNotContain("objectKey", body.GetRawText(), StringComparison.OrdinalIgnoreCase);   // where the file is kept is not part of the answer

        var now = DateTime.UtcNow;
        var main = DiskPath(root, $"uploads/{project}/{now:yyyy}/{now:MM}/{id:N}.png");
        Assert.Equal(png, await File.ReadAllBytesAsync(main));
        Assert.True(File.Exists(main.Replace(".png", "_thumb.webp")));
        Assert.Equal(2, FilesUnder(Path.Combine(factory.StoragePath, root)).Length);
    }

    [Fact]
    public async Task TheStoredRecord_HoldsTheStorageProfileAndProvider_ButNotTheClientsName()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var configId = (await SaveAsync(admin, project, "LocalDisk", new { rootPath = "r-" + project })).GetProperty("id").GetGuid();
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);

        var id = (await ReadAsync(await client.PostAsync(Upload, Form(TestFiles.Png(), "../../etc/passwd")))).GetProperty("file").GetProperty("id").GetGuid();

        var row = await factory.WithDbAsync(db => db.StoredFiles.SingleAsync(f => f.Id == id));
        Assert.Equal((configId, ProviderType.LocalDisk, "passwd"), (row.StorageProfileId, row.StorageProvider, row.OriginalFileName));
        Assert.DoesNotContain("passwd", row.ObjectKey);
        Assert.DoesNotContain("..", row.ObjectKey);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\system32\\evil.png", "evil.png")]
    public async Task APathTraversalFileName_NeverLeavesTheStorageFolder(string sent, string shown)
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);
        var before = FilesUnder(factory.StoragePath).Length;

        var response = await client.PostAsync(Upload, Form(TestFiles.Png(), sent));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(shown, (await ReadAsync(response)).GetProperty("file").GetProperty("originalFileName").GetString());
        var created = FilesUnder(factory.StoragePath).Length - before;
        Assert.Equal(2, created);   // the file and its thumbnail, and nothing anywhere else
        Assert.All(FilesUnder(factory.StoragePath).Where(p => p.Contains(root)), p => Assert.Contains(Path.Combine(root, "uploads", project), p));
        Assert.False(File.Exists(Path.Combine(factory.StoragePath, "etc", "passwd")));
    }

    [Fact]
    public async Task AnExecutable_CalledAPng_Is400_AndNothingIsWrittenAnywhere_ButTheRefusalIsLogged()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);

        var response = await client.PostAsync(Upload, Form(TestFiles.Executable(), "invoice.png", "image/png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not allowed", await response.Content.ReadAsStringAsync());
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, root)));
        Assert.False(await factory.WithDbAsync(db => db.StoredFiles.AnyAsync(f => f.ProjectCode == project)));
        var logs = await ReadAsync(await client.GetAsync($"{MediaLogs}?projectCode={project}"));
        var log = Assert.Single(logs.EnumerateArray());
        Assert.Equal(("Upload", "Failed"), (log.GetProperty("action").GetString(), log.GetProperty("status").GetString()));
        Assert.DoesNotContain("invoice", logs.GetRawText());
    }

    [Theory]
    [InlineData("script")]
    [InlineData("svg")]
    [InlineData("html")]
    public async Task ScriptsSvgAndHtml_AreRefusedWhateverTheyAreCalled(string kind)
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);
        var bytes = kind switch { "script" => TestFiles.Script(), "svg" => "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(), _ => "<html><body onload=alert(1)>"u8.ToArray() };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Upload, Form(bytes, "x.png", "image/png"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Upload, Form(bytes, "x.svg", "image/svg+xml"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Upload, Form(bytes, "x.bin", null))).StatusCode);
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, root)));
    }

    [Fact]
    public async Task APngCalledAJpeg_Is400()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);

        var response = await client.PostAsync(Upload, Form(TestFiles.Png(), "a.jpg", "image/jpeg"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("image/png", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AFileWithoutAnAllowedClaim_ButWithRealContent_IsAcceptedAsWhatItIs()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);

        var response = await client.PostAsync(Upload, Form(TestFiles.Pdf(), "report", contentType: null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var file = (await ReadAsync(response)).GetProperty("file");
        Assert.Equal(("application/pdf", false), (file.GetProperty("mimeType").GetString(), file.GetProperty("hasThumbnail").GetBoolean()));
        Assert.Single(FilesUnder(Path.Combine(factory.StoragePath, root)), p => p.EndsWith(".pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADecompressionBomb_Is400_AndNothingIsStored()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);

        var tooWide = await client.PostAsync(Upload, Form(TestFiles.Png(8001, 1), "wide.png"));

        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
        Assert.Contains("8001", await tooWide.Content.ReadAsStringAsync());
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, root)));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync(Upload, Form(TestFiles.Png(8000, 1), "limit.png"))).StatusCode);   // at the limit it is fine
    }

    [Fact]
    public async Task AFileOverTheConfiguredLimit_Is413_AndOneUnderItIsStored()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project, maxMb: 1);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);

        var over = await client.PostAsync(Upload, Form(TestFiles.Pdf(2 * 1024 * 1024), "big.pdf", "application/pdf"));
        var under = await client.PostAsync(Upload, Form(TestFiles.Pdf(512 * 1024), "small.pdf", "application/pdf"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal(HttpStatusCode.Created, under.StatusCode);
        Assert.Single(FilesUnder(Path.Combine(factory.StoragePath, root)));
    }

    [Fact]
    public async Task NoFile_Is400()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload);

        var response = await client.PostAsync(Upload, new MultipartFormDataContent { { new StringContent("true"), "isPublic" } });
        var empty = await client.PostAsync(Upload, Form([], "empty.png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task AProjectWithoutItsOwnStorage_UsesTheDefaultProjectsFolder_ButOwnsItsFiles()
    {
        var admin = await AdminAsync();
        var root = await LocalProjectAsync(admin, "default");
        var project = NewProject();
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);

        var response = await client.PostAsync(Upload, Form(TestFiles.Png(), "a.png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(project, (await ReadAsync(response)).GetProperty("file").GetProperty("projectCode").GetString());
        Assert.NotEmpty(FilesUnder(Path.Combine(factory.StoragePath, root, "uploads", project)));
    }

    // ------------------------------------------------------------------------------------------------ download

    [Fact]
    public async Task ADownload_ReturnsTheBytes_WithTheRealContentType_ETag_CacheControl_AndNosniff()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        var png = TestFiles.Png();
        var file = (await ReadAsync(await client.PostAsync(Upload, Form(png, "Photo.png")))).GetProperty("file");
        var id = file.GetProperty("id").GetGuid();

        var response = await client.GetAsync($"{Files}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{file.GetProperty("fileHashSha256").GetString()}\"", response.Headers.ETag!.Tag);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromSeconds(300), response.Headers.CacheControl.MaxAge);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("Photo.png", response.Content.Headers.ContentDisposition.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(png.Length, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task DownloadTrue_IsAnAttachment()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        var id = (await ReadAsync(await client.PostAsync(Upload, Form(TestFiles.Pdf(), "Report.pdf", "application/pdf")))).GetProperty("file").GetProperty("id").GetGuid();

        var response = await client.GetAsync($"{Files}/{id}?download=true");

        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task AMatchingETag_Is304_WithNoBody_AndIsNotLoggedAsADownload()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        var id = (await ReadAsync(await client.PostAsync(Upload, Form(TestFiles.Png(), "a.png")))).GetProperty("file").GetProperty("id").GetGuid();
        var etag = (await client.GetAsync($"{Files}/{id}")).Headers.ETag!.Tag;
        var downloadsBefore = await factory.WithDbAsync(db => db.MediaLogs.CountAsync(l => l.ProjectCode == project && l.Action == "Download"));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Files}/{id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(downloadsBefore, await factory.WithDbAsync(db => db.MediaLogs.CountAsync(l => l.ProjectCode == project && l.Action == "Download")));

        using var other = new HttpRequestMessage(HttpMethod.Get, $"{Files}/{id}");
        other.Headers.TryAddWithoutValidation("If-None-Match", "\"something-else\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(other)).StatusCode);
    }

    [Fact]
    public async Task TheThumbnail_IsAWebpNoLargerThan256_AndAPdfHasNone()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        var image = (await ReadAsync(await client.PostAsync(Upload, Form(TestFiles.Png(1024, 512), "wide.png")))).GetProperty("file").GetProperty("id").GetGuid();
        var pdf = (await ReadAsync(await client.PostAsync(Upload, Form(TestFiles.Pdf(), "a.pdf", "application/pdf")))).GetProperty("file").GetProperty("id").GetGuid();

        var thumb = await client.GetAsync($"{Files}/{image}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType!.MediaType);
        var bytes = await thumb.Content.ReadAsByteArrayAsync();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        using var codec = SkiaSharp.SKCodec.Create(SkiaSharp.SKData.CreateCopy(bytes));
        Assert.Equal((256, 128), (codec.Info.Width, codec.Info.Height));
        Assert.True(bytes.Length < TestFiles.Png(1024, 512).Length + 4096);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Files}/{pdf}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task AnUnknownFile_Is404()
    {
        var client = await ProjectClientAsync(NewProject(), ApiScopes.StorageRead);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Files}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Files}/{Guid.NewGuid()}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Files}/{Guid.NewGuid()}/metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await ProjectClientAsync(NewProject(), ApiScopes.StorageDelete)).DeleteAsync($"{Files}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task AFileThatIsGoneFromTheDisk_Is404_AndTheFailureIsLogged()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        var id = (await ReadAsync(await client.PostAsync(Upload, Form(TestFiles.Pdf(), "a.pdf", "application/pdf")))).GetProperty("file").GetProperty("id").GetGuid();
        foreach (var path in FilesUnder(Path.Combine(factory.StoragePath, root))) File.Delete(path);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Files}/{id}")).StatusCode);

        Assert.True(await factory.WithDbAsync(db => db.MediaLogs.AnyAsync(l => l.FileId == id && l.Action == "Download" && l.Status == "Failed")));
    }

    // ------------------------------------------------------------------------------------------------ access

    [Fact]
    public async Task WithoutAKey_401_WithoutTheScope_403()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await LocalProjectAsync(admin, project);
        var uploader = await ProjectClientAsync(project, ApiScopes.StorageUpload);
        var id = (await ReadAsync(await uploader.PostAsync(Upload, Form(TestFiles.Png(), "a.png")))).GetProperty("file").GetProperty("id").GetGuid();
        var anonymous = factory.ClientWithKey(null);
        var configOnly = await ProjectClientAsync(project, ApiScopes.ConfigRead);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Upload, Form(TestFiles.Png(), "a.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await configOnly.PostAsync(Upload, Form(TestFiles.Png(), "a.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Files)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await configOnly.GetAsync(Files)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Files}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await uploader.DeleteAsync($"{Files}/{id}")).StatusCode);   // upload is not delete
        Assert.Equal(HttpStatusCode.Forbidden, (await configOnly.GetAsync($"{Files}/{id}")).StatusCode);   // a private file needs a storage scope
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Files}/{id}")).StatusCode);
    }

    [Fact]
    public async Task APrivateFile_IsOnlyForItsOwnProject_AndThePlatformKey()
    {
        var admin = await AdminAsync();
        var mine = NewProject();
        var other = NewProject();
        await LocalProjectAsync(admin, mine);
        var owner = await ProjectClientAsync(mine, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete);
        var stranger = await ProjectClientAsync(other, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete);
        var id = (await ReadAsync(await owner.PostAsync(Upload, Form(TestFiles.Png(), "a.png")))).GetProperty("file").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{Files}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"{Files}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"{Files}/{id}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"{Files}/{id}/metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.DeleteAsync($"{Files}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Files}/{id}")).StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.StoredFiles.AnyAsync(f => f.Id == id)));   // the refused delete deleted nothing
    }

    [Fact]
    public async Task APublicFile_IsReadByAnyone_WithAPublicCacheControl_ButNotDeleted()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var owner = await ProjectClientAsync(project, ApiScopes.StorageUpload);
        var (file, _) = await UploadPngAsync(owner, project, root, isPublic: true);
        var id = file.GetProperty("id").GetGuid();
        Assert.True(file.GetProperty("isPublic").GetBoolean());
        var anonymous = factory.ClientWithKey(null);

        var response = await anonymous.GetAsync($"{Files}/{id}");
        var thumbnail = await anonymous.GetAsync($"{Files}/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl.MaxAge);
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Files}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Files}/{id}/metadata")).StatusCode);   // the file is public, its record is not
    }

    [Fact]
    public async Task AKeyCannotUploadForAnotherProject_ButThePlatformKeyCan()
    {
        var admin = await AdminAsync();
        var other = NewProject();
        var root = await LocalProjectAsync(admin, other);
        var client = await ProjectClientAsync(NewProject(), ApiScopes.StorageUpload);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Upload, Form(TestFiles.Png(), "a.png", project: other))).StatusCode);
        var response = await admin.PostAsync(Upload, Form(TestFiles.Png(), "a.png", project: other));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(other, (await ReadAsync(response)).GetProperty("file").GetProperty("projectCode").GetString());
        Assert.NotEmpty(FilesUnder(Path.Combine(factory.StoragePath, root, "uploads", other)));
    }

    [Fact]
    public async Task ABearerlessReadOnlyUploadScope_CanReadItsOwnFiles()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await LocalProjectAsync(admin, project);
        var uploader = await ProjectClientAsync(project, ApiScopes.StorageUpload);
        var id = (await ReadAsync(await uploader.PostAsync(Upload, Form(TestFiles.Png(), "a.png")))).GetProperty("file").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await uploader.GetAsync($"{Files}/{id}")).StatusCode);   // storage:upload also lets a key read its project's files
    }

    // ------------------------------------------------------------------------------------------------ list, delete, metadata, logs

    [Fact]
    public async Task TheList_IsPerProject_NewestFirst_AndPaged()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var other = NewProject();
        await LocalProjectAsync(admin, project);
        await LocalProjectAsync(admin, other);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        foreach (var name in new[] { "1.pdf", "2.pdf", "3.pdf" })
        {
            await client.PostAsync(Upload, Form(TestFiles.Pdf(), name, "application/pdf"));
            await Task.Delay(15);
        }

        await (await ProjectClientAsync(other, ApiScopes.StorageUpload)).PostAsync(Upload, Form(TestFiles.Pdf(), "x.pdf", "application/pdf"));

        var all = await ReadAsync(await client.GetAsync(Files));
        var page = await ReadAsync(await client.GetAsync($"{Files}?limit=1&offset=1"));

        Assert.Equal(["3.pdf", "2.pdf", "1.pdf"], all.EnumerateArray().Select(f => f.GetProperty("originalFileName").GetString()));
        Assert.Equal(["2.pdf"], page.EnumerateArray().Select(f => f.GetProperty("originalFileName").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Files}?projectCode={other}")).StatusCode);
        Assert.Equal(["x.pdf"], (await ReadAsync(await admin.GetAsync($"{Files}?projectCode={other}"))).EnumerateArray().Select(f => f.GetProperty("originalFileName").GetString()));
    }

    [Fact]
    public async Task ADelete_RemovesTheFile_TheThumbnail_AndTheRecord_AndTheLogOutlivesIt()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete);
        var (file, _) = await UploadPngAsync(client, project, root);
        var id = file.GetProperty("id").GetGuid();
        Assert.Equal(2, FilesUnder(Path.Combine(factory.StoragePath, root)).Length);

        var response = await client.DeleteAsync($"{Files}/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, root)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Files}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Files}/{id}")).StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.StoredFiles.AnyAsync(f => f.Id == id)));
        var actions = await factory.WithDbAsync(db => db.MediaLogs.Where(l => l.ProjectCode == project).OrderBy(l => l.CreatedAt).Select(l => l.Action + ":" + l.Status).ToListAsync());
        Assert.Contains("Delete:Success", actions);
    }

    [Fact]
    public async Task TheMetadata_HasNoStoragePath()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        var id = (await ReadAsync(await client.PostAsync(Upload, Form(TestFiles.Pdf(), "a.pdf", "application/pdf")))).GetProperty("file").GetProperty("id").GetGuid();

        var response = await client.GetAsync($"{Files}/{id}/metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("application/pdf", text);
        Assert.DoesNotContain("uploads/", text);
        Assert.DoesNotContain(factory.StoragePath, text);
    }

    [Fact]
    public async Task TheMediaLog_RecordsUploadThumbnailDownloadAndDelete_OfTheKeysProjectOnly_WithNoNamesOrPaths()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var other = NewProject();
        var root = await LocalProjectAsync(admin, project);
        await LocalProjectAsync(admin, other);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete);
        var (file, _) = await UploadPngAsync(client, project, root, name: "Confidential-plans.png");
        var id = file.GetProperty("id").GetGuid();
        await client.GetAsync($"{Files}/{id}");
        await client.DeleteAsync($"{Files}/{id}");
        await (await ProjectClientAsync(other, ApiScopes.StorageUpload)).PostAsync(Upload, Form(TestFiles.Png(), "other.png"));

        var response = await client.GetAsync(MediaLogs);
        var text = await response.Content.ReadAsStringAsync();
        var rows = JsonDocument.Parse(text).RootElement.EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Delete", "Download", "Thumbnail", "Upload"], rows.Select(r => r.GetProperty("action").GetString()).Order());
        Assert.All(rows, r => Assert.Equal(project, r.GetProperty("projectCode").GetString()));
        Assert.DoesNotContain("Confidential", text);
        Assert.DoesNotContain("uploads/", text);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{MediaLogs}?projectCode={other}")).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ a project changes its storage

    [Fact]
    public async Task AfterAProjectSwitchesToMock_ItsOlderFilesAreStillReadFromTheDisk_AndNewOnesGoToMemory()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var root = await LocalProjectAsync(admin, project);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete);
        var png = TestFiles.Png();
        var oldId = (await ReadAsync(await client.PostAsync(Upload, Form(png, "old.png")))).GetProperty("file").GetProperty("id").GetGuid();

        await SaveAsync(admin, project, "Mock", new { });   // a new version of the same configuration, now in memory
        var fresh = await ReadAsync(await client.PostAsync(Upload, Form(png, "new.png")));
        var freshId = fresh.GetProperty("file").GetProperty("id").GetGuid();

        Assert.Equal("Mock", fresh.GetProperty("file").GetProperty("storageProvider").GetString());
        Assert.Contains(factory.MockStorage.Keys, k => k.Contains(freshId.ToString("N")));
        Assert.Equal(png, await (await client.GetAsync($"{Files}/{oldId}")).Content.ReadAsByteArrayAsync());   // still from the disk
        Assert.Equal(png, await (await client.GetAsync($"{Files}/{freshId}")).Content.ReadAsByteArrayAsync());  // from memory

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Files}/{oldId}")).StatusCode);
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, root)));   // removed from the disk it lived on
        Assert.Contains(factory.MockStorage.Keys, k => k.Contains(freshId.ToString("N")));   // memory untouched
    }

    // ------------------------------------------------------------------------------------------------ S3 (through a stand-in for the service)

    [Fact]
    public async Task AnS3Upload_PutsTheObjectInTheBucket_SignedWithTheStoredKey_AndNeverSendsTheSecret()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await SaveAsync(admin, project, "S3", new { bucket = "media-bucket", region = "us-east-1", serviceUrl = "https://s3.example.test", forcePathStyle = true, accessKeyId = "test-access-key-id", publicUrlBase = "https://cdn.example.com/m" }, StoredS3Secret);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        factory.S3.Calls.Clear();

        var response = await client.PostAsync(Upload, Form(TestFiles.Png(), "a.png", isPublic: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("S3", body.GetProperty("file").GetProperty("storageProvider").GetString());
        Assert.StartsWith("https://cdn.example.com/m/uploads/" + project + "/", body.GetProperty("publicUrl").GetString());
        var puts = factory.S3.Calls.Where(c => c.Request.Method == HttpMethod.Put).ToList();
        Assert.Equal(2, puts.Count);   // the file and its thumbnail
        Assert.All(puts, p =>
        {
            Assert.Equal("s3.example.test", p.Request.RequestUri!.Host);
            Assert.StartsWith($"/media-bucket/uploads/{project}/", p.Request.RequestUri.AbsolutePath);
            Assert.Contains("Credential=test-access-key-id/", p.Request.Headers.GetValues("Authorization").Single());
        });
        Assert.DoesNotContain(factory.S3.Calls, c => c.Request.RequestUri!.ToString().Contains(StoredS3Secret) || c.Request.Headers.ToString().Contains(StoredS3Secret));
        Assert.DoesNotContain(StoredS3Secret, await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------------------------------------ P0: testing never touches what is saved

    private async Task<List<(Guid Id, int Version, ConfigurationStatus Status, string Settings, string? Secret, DateTimeOffset? TestedAt, TestStatus? TestStatus)>> SnapshotAsync(string project) =>
        await factory.WithDbAsync(async db => (await db.ChannelConfigurations.Where(c => c.ProjectCode == project).OrderBy(c => c.Version).ToListAsync())
            .Select(c => (c.Id, c.Version, c.Status, c.SettingsJson, c.EncryptedSecret, c.LastTestedAt, (TestStatus?)c.LastTestStatus)).ToList());

    [Fact]
    public async Task ATestFromMemory_ChangesNothingThatIsSaved_AndLeavesNoFileBehind()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await SaveAsync(admin, project, "LocalDisk", new { rootPath = "saved-" + project });
        var before = await SnapshotAsync(project);
        var configCount = await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync());
        var tried = "tried-" + project;

        var response = await admin.PostAsJsonAsync(Test, new { providerType = "LocalDisk", settings = new { rootPath = tried } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.False(body.GetProperty("usedSavedConfiguration").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("configurationId").ValueKind);
        Assert.DoesNotContain(factory.StoragePath, body.GetRawText());   // no server path in the answer
        Assert.Equal(before, await SnapshotAsync(project));
        Assert.Equal(configCount, await factory.WithDbAsync(db => db.ChannelConfigurations.CountAsync()));
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, tried)));   // the canary was deleted again (the folder it was made in may remain)
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, "saved-" + project)));
    }

    [Fact]
    public async Task ATestFromMemory_OfS3_UsesTheTypedSecret_NeverTheStoredOne_AndNeverRepeatsIt()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await SaveAsync(admin, project, "S3", new { bucket = "media-bucket", region = "us-east-1", serviceUrl = "https://s3.example.test", forcePathStyle = true, accessKeyId = "test-access-key-id" }, StoredS3Secret);
        var before = await SnapshotAsync(project);
        factory.S3.Calls.Clear();
        factory.S3.Respond = request => request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("list-type")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<?xml version=\"1.0\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>media-bucket</Name><KeyCount>0</KeyCount><MaxKeys>1</MaxKeys><IsTruncated>false</IsTruncated></ListBucketResult>") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x42]) };

        var response = await admin.PostAsJsonAsync(Test, new
        {
            providerType = "S3",
            settings = new { bucket = "media-bucket", region = "us-east-1", serviceUrl = "https://s3.example.test", forcePathStyle = true, accessKeyId = "test-access-key-id" },
            secret = S3Secret,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("success").GetBoolean());
        Assert.Equal(["GET", "PUT", "GET", "DELETE"], factory.S3.Calls.Select(c => c.Request.Method.Method));
        Assert.DoesNotContain(S3Secret, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(project));
        factory.S3.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ATestFromMemory_NeverBorrowsAStoredSecret()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        await SaveAsync(admin, project, "S3", new { bucket = "media-bucket", region = "us-east-1", accessKeyId = "test-access-key-id" }, StoredS3Secret);
        factory.S3.Calls.Clear();

        var response = await admin.PostAsJsonAsync(Test, new { providerType = "S3", settings = new { bucket = "media-bucket", region = "us-east-1", accessKeyId = "test-access-key-id" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.S3.Calls);
    }

    [Theory]
    [InlineData("http://169.254.169.254")]    // the cloud metadata address
    [InlineData("http://224.0.0.1")]
    public async Task AnS3ServiceUrlThatPointsAtAForbiddenAddress_IsNeverContacted(string serviceUrl)
    {
        var admin = await AdminAsync();
        factory.S3.Calls.Clear();

        var response = await admin.PostAsJsonAsync(Test, new
        {
            providerType = "S3", settings = new { bucket = "media-bucket", serviceUrl, forcePathStyle = true, accessKeyId = "test-access-key-id" }, secret = S3Secret,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("not allowed", body.GetProperty("message").GetString());
        Assert.Empty(factory.S3.Calls);
        Assert.DoesNotContain(S3Secret, body.GetRawText());
    }

    [Fact]
    public async Task WithoutPathStyle_ABucketSubdomainThatResolvesToTheMetadataAddress_IsNeverContacted_ByTestOrUpload()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        // Path style off (the form's default). "evil-bucket.s3.example.test" is a wildcard-DNS name that points at 169.254.169.254.
        var settings = new { bucket = "evil-bucket", region = "us-east-1", serviceUrl = "https://s3.example.test", accessKeyId = "test-access-key-id" };
        await SaveAsync(admin, project, "S3", settings, StoredS3Secret);
        var client = await ProjectClientAsync(project, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete);
        factory.S3.Calls.Clear();

        var test = await admin.PostAsJsonAsync(Test, new { providerType = "S3", settings, secret = S3Secret });
        var upload = await client.PostAsync(Upload, Form(TestFiles.Png(), "a.png"));

        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var body = await ReadAsync(test);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("evil-bucket.s3.example.test", body.GetProperty("message").GetString());
        Assert.Contains("not allowed", body.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.BadGateway, upload.StatusCode);
        Assert.Empty(factory.S3.Calls);   // neither the service address nor the bucket name received a request

        // With path style on, the same service is reached by its own address only, so the bucket name is not a host at all.
        var pathStyle = new { bucket = "evil-bucket", region = "us-east-1", serviceUrl = "https://s3.example.test", forcePathStyle = true, accessKeyId = "test-access-key-id" };
        await admin.PostAsJsonAsync(Test, new { providerType = "S3", settings = pathStyle, secret = S3Secret });
        Assert.NotEmpty(factory.S3.Calls);
        Assert.All(factory.S3.Calls, c => Assert.Equal("s3.example.test", c.Request.RequestUri!.Host));
    }

    [Fact]
    public async Task ATestOfASavedVersion_UsesItsSecret_AndRecordsTheResultOnThatVersionOnly()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        var first = await SaveAsync(admin, project, "LocalDisk", new { rootPath = "first-" + project });
        var draft = await SaveAsync(admin, project, "LocalDisk", new { rootPath = "draft-" + project }, activate: false);

        var response = await admin.PostAsJsonAsync(Test, new { configurationId = draft.GetProperty("id").GetGuid() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("usedSavedConfiguration").GetBoolean());
        var rows = await SnapshotAsync(project);
        Assert.Equal(TestStatus.Success, rows.Single(r => r.Id == draft.GetProperty("id").GetGuid()).TestStatus);
        Assert.Equal(TestStatus.Untested, rows.Single(r => r.Id == first.GetProperty("id").GetGuid()).TestStatus);
        Assert.Equal(ConfigurationStatus.Active, rows.Single(r => r.Id == first.GetProperty("id").GetGuid()).Status);   // testing the draft activated nothing
        Assert.True(Directory.Exists(Path.Combine(factory.StoragePath, "draft-" + project)));
    }

    [Fact]
    public async Task AFailedSavedTest_IsRecordedFailed_AndStillAnswers200()
    {
        var admin = await AdminAsync();
        var project = NewProject();
        Directory.CreateDirectory(factory.StoragePath);
        var blocker = "blocker-" + project;
        await File.WriteAllTextAsync(Path.Combine(factory.StoragePath, blocker), "a file where a folder is needed");
        var saved = await SaveAsync(admin, project, "LocalDisk", new { rootPath = blocker + "/sub" });

        var response = await admin.PostAsJsonAsync(Test, new { configurationId = saved.GetProperty("id").GetGuid() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(factory.StoragePath, body.GetRawText());
        Assert.Equal(TestStatus.Failed, (await SnapshotAsync(project)).Single().TestStatus);
    }

    [Fact]
    public async Task TheTestEndpoint_NeedsConfigWrite_And404sAnUnknownId_And400sBadInput()
    {
        var admin = await AdminAsync();
        var reader = await ProjectClientAsync(NewProject(), ApiScopes.StorageUpload, ApiScopes.ConfigRead);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Test, new { providerType = "Mock" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).PostAsJsonAsync(Test, new { providerType = "Mock" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Test, new { configurationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Test, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Test, new { providerType = "Smtp" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Test, new { providerType = "LocalDisk" })).StatusCode);   // no rootPath
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Test, new { providerType = "LocalDisk", settings = new { rootPath = "../x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Test, new { providerType = "S3", settings = new { bucket = "media-bucket", region = "us-east-1", accessKeyId = "test-access-key-id" }, secret = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(Test, new { providerType = "Mock" })).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ saving a storage configuration

    [Theory]
    [InlineData("LocalDisk", """{"rootPath":"../outside"}""", null)]
    [InlineData("LocalDisk", """{"maxFileSizeMb":500}""", null)]
    [InlineData("S3", """{"bucket":"BAD BUCKET"}""", "Fake-S3-Secret-Access-Key-0123456789")]
    [InlineData("S3", """{"bucket":"media-bucket","serviceUrl":"http://user:pw@minio:9000"}""", "Fake-S3-Secret-Access-Key-0123456789")]
    [InlineData("S3", """{"bucket":"media-bucket"}""", "short")]
    public async Task SavingBadStorageSettings_Is400_AndNothingIsStored(string provider, string settings, string? secret)
    {
        var project = NewProject();

        var response = await (await AdminAsync()).PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Storage", providerType = provider, name = "Storage", settings = JsonDocument.Parse(settings).RootElement, secret });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await factory.WithDbAsync(db => db.ChannelConfigurations.AnyAsync(c => c.ProjectCode == project)));
    }

    [Fact]
    public async Task ChangingTheProviderFromLocalDiskToS3_WithoutASecret_Is400()
    {
        var project = NewProject();
        var admin = await AdminAsync();
        await SaveAsync(admin, project, "LocalDisk", new { rootPath = "r" });

        var refused = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Storage", providerType = "S3", name = "Storage", settings = new { bucket = "media-bucket" } });
        var accepted = await admin.PostAsJsonAsync(Configs, new { projectCode = project, channelType = "Storage", providerType = "S3", name = "Storage", settings = new { bucket = "media-bucket" }, secret = S3Secret });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.DoesNotContain(S3Secret, await accepted.Content.ReadAsStringAsync());
    }
}

/// <summary>On a host of its own, so that no other test has given the "default" project a storage configuration.</summary>
public class StorageApiWithoutConfigurationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task NoStorageConfigurationAnywhere_Is409_AndTheRefusalIsLogged()
    {
        var project = "p" + Guid.NewGuid().ToString("N")[..8];
        var client = factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, ApiScopes.StorageUpload, ApiScopes.StorageRead)).PlainKey);
        var form = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()), "file", "a.png" } };

        var response = await client.PostAsync("/api/v1/storage/upload", form);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("no active storage configuration", await response.Content.ReadAsStringAsync());
        Assert.True(await factory.WithDbAsync(db => db.MediaLogs.AnyAsync(l => l.ProjectCode == project && l.Status == "Failed")));
    }
}

public class StorageApiDevelopmentTests(DevelopmentApiFactory factory) : IClassFixture<DevelopmentApiFactory>
{
    [Fact]
    public async Task InDevelopment_ABadRequest_IsStill400_NotACrash()
    {
        var (plain, _) = await factory.CreateKeyForProjectAsync("default", null, ApiScopes.StorageUpload, ApiScopes.ConfigWrite);
        var client = factory.ClientWithKey(plain);

        var notMultipart = await client.PostAsync("/api/v1/storage/upload", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        var noFile = await client.PostAsync("/api/v1/storage/upload", new MultipartFormDataContent { { new StringContent("x"), "isPublic" } });
        var badTest = await client.PostAsJsonAsync("/api/v1/storage/test", new { providerType = "S3", settings = new { bucket = "BAD BUCKET" }, secret = "short" });
        var malformed = await client.PostAsync("/api/v1/storage/test", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badTest.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.True((int)notMultipart.StatusCode is 400 or 415, $"got {(int)notMultipart.StatusCode}");
    }
}
