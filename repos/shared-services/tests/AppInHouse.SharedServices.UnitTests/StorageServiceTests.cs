using System.Security.Cryptography;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.SharedServices.UnitTests;

public class StorageServiceTests
{
    private const string LocalSettings = """{"rootPath":"uploads","maxFileSizeMb":1}""";
    private const string S3Settings = """{"bucket":"media-bucket","region":"us-east-1","accessKeyId":"test-access-key-id","publicUrlBase":"https://cdn.example.com/media"}""";
    private const string S3Secret = "fake-secret-access-key-for-tests-0123456789";

    private sealed class Fixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeStorageProvider Disk { get; } = new(ProviderType.LocalDisk);
        public FakeStorageProvider S3 { get; } = new(ProviderType.S3);
        public FakeStorageProvider Mock { get; } = new(ProviderType.Mock);
        public FakeStoredFileRepository Files { get; } = new();
        public FakeMediaLogRepository Logs { get; } = new();
        public ThrowingUnitOfWork Unit { get; } = new();
        public FakeThumbnails Thumbs { get; } = new();
        public StorageService Service { get; }

        public Fixture(bool withS3Provider = true)
        {
            var providers = new List<FakeStorageProvider> { Disk, Mock };
            if (withS3Provider) providers.Add(S3);
            Service = new StorageService(Configs.Manager, providers, Thumbs, Files, Logs, Unit, Configs.Clock, NullLogger<StorageService>.Instance);
        }

        public async Task<ChannelConfigurationDto> ActivateAsync(string project, ProviderType provider, string? settings = null, string? secret = null, string name = "Storage")
        {
            settings ??= provider switch { ProviderType.LocalDisk => LocalSettings, ProviderType.S3 => S3Settings, _ => "{}" };
            secret ??= provider == ProviderType.S3 ? S3Secret : null;
            var saved = await Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand(project, ChannelType.Storage, provider, name, settings, secret));
            return await Configs.Manager.ActivateAsync(saved.Id);
        }

        public Task<UploadFileResult> UploadAsync(byte[] bytes, string project = "linguabridge", string name = "photo.png", string? contentType = "image/png", bool isPublic = false) =>
            Service.UploadAsync(new UploadFileCommand(project, name, contentType, new MemoryStream(bytes), isPublic));
    }

    // ------------------------------------------------------------------------------------------------ upload

    [Fact]
    public async Task APngUpload_IsStored_WithAServerMadeKey_AHashAndAThumbnail_AndLogged()
    {
        var f = new Fixture();
        var config = await f.ActivateAsync("default", ProviderType.LocalDisk);
        var png = TestFiles.FakePng(500);

        var result = await f.UploadAsync(png, name: "Holiday.PNG");

        Assert.True(result.Success);
        var dto = result.File!;
        Assert.Equal(("linguabridge", "image/png", 500L, "Holiday.PNG", ProviderType.LocalDisk), (dto.ProjectCode, dto.MimeType, dto.FileSizeBytes, dto.OriginalFileName, dto.StorageProvider));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(png)), dto.FileHashSha256);
        Assert.True(dto.HasThumbnail);
        Assert.Equal($"/api/v1/storage/files/{dto.Id}", dto.Url);
        Assert.Equal($"/api/v1/storage/files/{dto.Id}/thumbnail", dto.ThumbnailUrl);

        var row = Assert.Single(f.Files.Rows);
        Assert.Matches($@"^uploads/linguabridge/2026/11/{dto.Id:N}\.png$", row.ObjectKey);
        Assert.Equal(config.Id, row.StorageProfileId);   // the profile it was written with, so it can be found again after a change
        Assert.Equal([row.ObjectKey, row.ThumbnailObjectKey], f.Disk.Puts.Select(p => p.Key));
        Assert.Equal(png, f.Disk.Objects[row.ObjectKey]);
        Assert.Equal(f.Thumbs.Result, f.Disk.Objects[row.ThumbnailObjectKey]);
        Assert.Equal(["image/png", "image/webp"], f.Disk.Puts.Select(p => p.Mime));
        Assert.Equal([("Upload", "Success"), ("Thumbnail", "Success")], f.Logs.Rows.Select(l => (l.Action, l.Status)));
        Assert.All(f.Logs.Rows, l => Assert.Equal(row.Id, l.FileId));
    }

    [Theory]
    [InlineData("jpeg", "image/jpeg", true)]
    [InlineData("pdf", "application/pdf", false)]
    [InlineData("gif", "image/gif", false)]
    [InlineData("mp3", "audio/mpeg", false)]
    [InlineData("ogg", "audio/ogg", false)]
    [InlineData("wav", "audio/wav", false)]
    [InlineData("webp", "image/webp", true)]
    public async Task EveryAllowedType_IsAccepted_AndOnlyPngJpegAndWebpGetAThumbnail(string kind, string mime, bool thumbnail)
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var bytes = kind switch
        {
            "jpeg" => TestFiles.FakeJpeg(), "pdf" => TestFiles.FakePdf(), "gif" => TestFiles.FakeGif(), "mp3" => TestFiles.FakeMp3(),
            "ogg" => TestFiles.FakeOgg(), "wav" => TestFiles.FakeWav(), _ => TestFiles.FakeWebp(),
        };

        var result = await f.UploadAsync(bytes, name: "f", contentType: mime);

        Assert.True(result.Success);
        Assert.Equal(mime, result.File!.MimeType);
        Assert.Equal(thumbnail, result.File.HasThumbnail);
        Assert.Equal(thumbnail ? 1 : 0, f.Thumbs.Calls);
        Assert.Equal(thumbnail ? 2 : 1, f.Disk.Puts.Count);
    }

    [Fact]
    public async Task AnExecutable_CalledAPng_IsRefused_AndNothingIsStoredOrRecorded_ButTheRefusalIsLogged()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.UploadAsync(TestFiles.Executable(), name: "invoice.png", contentType: "image/png"));

        Assert.Contains("not allowed", error.Errors["Content"][0]);
        Assert.Empty(f.Disk.Puts);
        Assert.Empty(f.Files.Rows);
        Assert.Equal(0, f.Thumbs.Calls);
        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal(("Upload", "Failed", (Guid?)null), (log.Action, log.Status, log.FileId));
    }

    [Theory]
    [InlineData("script")]
    [InlineData("svg")]
    [InlineData("html")]
    public async Task AScriptAnSvgOrAnHtmlPage_CalledAnImage_IsRefused(string kind)
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var bytes = kind switch
        {
            "script" => TestFiles.Script(),
            "svg" => "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(),
            _ => "<html><body onload=alert(1)>"u8.ToArray(),
        };

        await Assert.ThrowsAsync<ValidationException>(() => f.UploadAsync(bytes, name: "pic.png", contentType: "image/png"));

        Assert.Empty(f.Disk.Puts);
    }

    [Fact]
    public async Task APngCalledAJpeg_IsRefused_BecauseItIsNotWhatItSays()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.UploadAsync(TestFiles.FakePng(), name: "a.jpg", contentType: "image/jpeg"));

        Assert.Contains("image/png", error.Errors["ContentType"][0]);
        Assert.Empty(f.Disk.Puts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("application/octet-stream")]
    public async Task WithoutAUsefulContentType_TheRealTypeIsUsed(string? declared)
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);

        var result = await f.UploadAsync(TestFiles.FakePdf(), name: "doc", contentType: declared);

        Assert.Equal("application/pdf", result.File!.MimeType);
        Assert.EndsWith(".pdf", f.Files.Rows.Single().ObjectKey);   // the extension follows the real type, not the name
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\system32\\evil.dll", "evil.dll")]
    [InlineData("/etc/shadow", "shadow")]
    public async Task APathTraversalName_IsCleaned_AndNeverShapesTheKey(string sent, string shown)
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);

        var result = await f.UploadAsync(TestFiles.FakePng(), name: sent);

        Assert.Equal(shown, result.File!.OriginalFileName);
        var key = f.Files.Rows.Single().ObjectKey;
        Assert.DoesNotContain("..", key);
        Assert.DoesNotContain("passwd", key);
        Assert.DoesNotContain("evil", key);
        Assert.DoesNotContain("shadow", key);
        Assert.StartsWith("uploads/linguabridge/", key);
        Assert.All(f.Disk.Puts, p => Assert.StartsWith("uploads/linguabridge/", p.Key));
    }

    [Fact]
    public async Task TheKeysOfTwoUploadsOfTheSameFile_Differ()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);

        await f.UploadAsync(TestFiles.FakePng(), name: "same.png");
        await f.UploadAsync(TestFiles.FakePng(), name: "same.png");

        Assert.Equal(2, f.Files.Rows.Select(r => r.ObjectKey).Distinct().Count());
    }

    [Fact]
    public async Task AnEmptyFile_IsRefused()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.UploadAsync([]));

        Assert.Contains("empty", error.Errors["Content"][0]);
    }

    [Fact]
    public async Task AFileOverTheLimit_IsTooLarge_AtTheLimitItIsNot_AndTheLimitIsEnforcedWhileReading()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);   // maxFileSizeMb = 1
        const int limit = 1024 * 1024;

        var atLimit = await f.UploadAsync(TestFiles.FakePdf(limit), name: "a.pdf", contentType: "application/pdf");
        var over = await f.UploadAsync(TestFiles.FakePdf(limit + 1), name: "a.pdf", contentType: "application/pdf");

        Assert.True(atLimit.Success);
        Assert.Equal((false, StorageFailure.TooLarge), (over.Success, over.Failure));
        Assert.Contains("1 MB", over.Error);
        Assert.Single(f.Files.Rows);
        Assert.DoesNotContain(f.Disk.Puts, p => p.Config is null);

        // A stream that never ends: the service stops reading at the limit instead of buffering it all (and does not trust a length).
        var endless = new EndlessStream(TestFiles.FakePdf(16));
        var cut = await f.Service.UploadAsync(new UploadFileCommand("linguabridge", "big.pdf", "application/pdf", endless));
        Assert.Equal(StorageFailure.TooLarge, cut.Failure);
        Assert.True(endless.BytesGiven <= limit + 81920, $"read {endless.BytesGiven} bytes");
    }

    [Fact]
    public async Task TheDefaultLimit_IsFifteenMegabytes_AndTheHardCapIsFifty()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk, settings: """{"rootPath":"u"}""");

        var ok = await f.UploadAsync(TestFiles.FakePdf(15 * 1024 * 1024), name: "a.pdf", contentType: "application/pdf");
        var over = await f.UploadAsync(TestFiles.FakePdf(15 * 1024 * 1024 + 1), name: "a.pdf", contentType: "application/pdf");

        Assert.True(ok.Success);
        Assert.Equal(StorageFailure.TooLarge, over.Failure);
    }

    [Fact]
    public async Task ABombOfAnImage_IsRefused_BeforeAnythingIsStored()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        f.Thumbs.Failure = new ImageRejectedException("The image is 60000x60000 pixels; at most 8000x8000 is accepted.");

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.UploadAsync(TestFiles.FakePng()));

        Assert.Contains("60000x60000", error.Errors["Content"][0]);
        Assert.Empty(f.Disk.Puts);
        Assert.Empty(f.Files.Rows);
        Assert.Equal("Failed", Assert.Single(f.Logs.Rows).Status);
    }

    [Fact]
    public async Task APublicFile_WithAPublicBase_GetsAPublicUrl_APrivateOneDoesNot()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.S3);

        var publicUpload = await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf", isPublic: true);
        var privateUpload = await f.UploadAsync(TestFiles.FakePdf(), name: "b.pdf", contentType: "application/pdf");

        Assert.Equal($"https://cdn.example.com/media/{f.Files.Rows[0].ObjectKey}", publicUpload.PublicUrl);
        Assert.True(publicUpload.File!.IsPublic);
        Assert.Null(privateUpload.PublicUrl);
        Assert.False(privateUpload.File!.IsPublic);
    }

    [Fact]
    public async Task AnS3Upload_UsesTheS3Provider_WithTheDecryptedSecret()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.S3);

        var result = await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf");

        Assert.Equal(ProviderType.S3, result.File!.StorageProvider);
        Assert.Equal(S3Secret, Assert.Single(f.S3.Puts).Config.Secret);
        Assert.Empty(f.Disk.Puts);
    }

    // ------------------------------------------------------------------------------------------------ which storage

    [Fact]
    public async Task AProjectWithoutItsOwnStorage_UsesTheDefaultProjects_AndTheFileBelongsToTheProject()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);

        var result = await f.UploadAsync(TestFiles.FakePdf(), project: "shop", name: "a.pdf", contentType: "application/pdf");

        Assert.Equal("shop", result.File!.ProjectCode);
        Assert.StartsWith("uploads/shop/", f.Files.Rows.Single().ObjectKey);
        Assert.Equal("default", f.Disk.Puts.Single().Config.ProjectCode);
    }

    [Fact]
    public async Task TheProjectsOwnStorage_WinsOverTheDefault()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        await f.ActivateAsync("shop", ProviderType.Mock);

        await f.UploadAsync(TestFiles.FakePdf(), project: "shop", name: "a.pdf", contentType: "application/pdf");

        Assert.Single(f.Mock.Puts);
        Assert.Empty(f.Disk.Puts);
    }

    [Fact]
    public async Task OnlyTheActiveVersion_IsUsed_NotADraft()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        await f.Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("default", ChannelType.Storage, ProviderType.Mock, "Storage", "{}"));   // a draft

        await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf");

        Assert.Single(f.Disk.Puts);
        Assert.Empty(f.Mock.Puts);
    }

    [Fact]
    public async Task NoStorageAnywhere_IsReported_AndLogged()
    {
        var f = new Fixture();

        var result = await f.UploadAsync(TestFiles.FakePdf(), project: "shop");

        Assert.Equal((false, StorageFailure.NoConfiguration), (result.Success, result.Failure));
        Assert.Contains("shop", result.Error);
        Assert.Contains("default", result.Error);
        Assert.Equal(("Upload", "Failed"), (f.Logs.Rows.Single().Action, f.Logs.Rows.Single().Status));
    }

    [Fact]
    public async Task AnEmailConfiguration_IsNotAStorageConfiguration()
    {
        var f = new Fixture();
        var email = await f.Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("default", ChannelType.Email, ProviderType.Mock, "Mail", "{}"));
        await f.Configs.Manager.ActivateAsync(email.Id);

        Assert.Equal(StorageFailure.NoConfiguration, (await f.UploadAsync(TestFiles.FakePdf())).Failure);
    }

    [Fact]
    public async Task AnIncompleteConfiguration_IsReported_WithoutStoringAnything()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk, settings: "{}");

        var result = await f.UploadAsync(TestFiles.FakePdf());

        Assert.Equal(StorageFailure.ProviderFailure, result.Failure);
        Assert.Contains("rootPath is required", result.Error);
        Assert.Empty(f.Disk.Puts);
    }

    [Fact]
    public async Task AProviderThatIsNotAvailable_FailsClearly()
    {
        var f = new Fixture(withS3Provider: false);
        await f.ActivateAsync("default", ProviderType.S3);

        var result = await f.UploadAsync(TestFiles.FakePdf());

        Assert.Equal(StorageFailure.ProviderFailure, result.Failure);
        Assert.Contains("S3", result.Error);
    }

    [Fact]
    public async Task ACredentialThatCannotBeRead_IsAFailedUpload_WithAMessageToReEnterIt()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.S3);
        f.Configs.Repository.Rows.Single().GetType().GetProperty(nameof(ChannelConfiguration.EncryptedSecret))!.SetValue(f.Configs.Repository.Rows.Single(), "not-protected-by-this-key-ring");

        var result = await f.UploadAsync(TestFiles.FakePdf());

        Assert.Equal(StorageFailure.ProviderFailure, result.Failure);
        Assert.Contains("cannot be read", result.Error);
        Assert.Empty(f.S3.Puts);
    }

    // ------------------------------------------------------------------------------------------------ failures while writing

    [Fact]
    public async Task AWriteThatFails_IsAFailedUpload_WithTheSecretRemoved_AndNoRecord()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.S3);
        f.S3.PutFailure = new IOException($"refused using {S3Secret}");

        var result = await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf");

        Assert.Equal(StorageFailure.ProviderFailure, result.Failure);
        Assert.DoesNotContain(S3Secret, result.Error);
        Assert.Empty(f.Files.Rows);
        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal("Failed", log.Status);
        Assert.DoesNotContain(S3Secret, log.ErrorMessage);
    }

    [Fact]
    public async Task WhenTheThumbnailCannotBeWritten_TheFileIsTakenBackOut()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        f.Disk.FailThumbnailPut = true;

        var result = await f.UploadAsync(TestFiles.FakePng());

        Assert.Equal(StorageFailure.ProviderFailure, result.Failure);
        Assert.Empty(f.Disk.Objects);          // the file written first was deleted again
        Assert.Single(f.Disk.Deleted);
        Assert.Empty(f.Files.Rows);
    }

    [Fact]
    public async Task WhenTheRecordCannotBeSaved_TheWrittenObjectsAreTakenBackOut_AndTheErrorIsNotHidden()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        f.Unit.Failure = new InvalidOperationException("database is down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.UploadAsync(TestFiles.FakePng()));

        Assert.Empty(f.Disk.Objects);
        Assert.Equal(2, f.Disk.Deleted.Count);   // the file and its thumbnail
    }

    // ------------------------------------------------------------------------------------------------ reading

    [Fact]
    public async Task AFile_IsReadBack_WithItsTypeAndSize_AndTheDownloadIsLogged()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var png = TestFiles.FakePng(321);
        var id = (await f.UploadAsync(png)).File!.Id;
        f.Logs.Rows.Clear();

        await using var download = await f.Service.OpenAsync(id, thumbnail: false);

        Assert.NotNull(download);
        Assert.Equal(("image/png", 321L), (download!.ContentType, download.Length));
        using var copy = new MemoryStream();
        await download.Content.CopyToAsync(copy);
        Assert.Equal(png, copy.ToArray());
        Assert.Equal([("Download", "Success")], f.Logs.Rows.Select(l => (l.Action, l.Status)));
    }

    [Fact]
    public async Task AThumbnail_IsReadBackAsWebp_AndViewingItIsNotLogged()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var id = (await f.UploadAsync(TestFiles.FakePng())).File!.Id;
        f.Logs.Rows.Clear();

        await using var thumb = await f.Service.OpenAsync(id, thumbnail: true);

        Assert.Equal("image/webp", thumb!.ContentType);
        Assert.Empty(f.Logs.Rows);
    }

    [Fact]
    public async Task ReadingWithoutLoggingTheDownload_WritesNoDownloadLog_ButStillGivesTheFile()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var id = (await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf")).File!.Id;
        f.Logs.Rows.Clear();

        await using (var continued = await f.Service.OpenAsync(id, thumbnail: false, logDownload: false)) Assert.NotNull(continued);   // the rest of a ranged download
        Assert.Empty(f.Logs.Rows);

        await using (var first = await f.Service.OpenAsync(id, thumbnail: false)) Assert.NotNull(first);   // the default is to log
        Assert.Equal([("Download", "Success")], f.Logs.Rows.Select(l => (l.Action, l.Status)));
    }

    [Fact]
    public async Task AFailedRead_IsLogged_EvenWhenSuccessIsNot()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var id = (await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf")).File!.Id;
        f.Disk.Objects.Clear();
        f.Logs.Rows.Clear();

        Assert.Null(await f.Service.OpenAsync(id, thumbnail: false, logDownload: false));

        Assert.Equal([("Download", "Failed")], f.Logs.Rows.Select(l => (l.Action, l.Status)));
    }

    [Fact]
    public async Task AFileWithoutAThumbnail_HasNoThumbnailToOpen()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var id = (await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf")).File!.Id;

        Assert.Null(await f.Service.OpenAsync(id, thumbnail: true));
    }

    [Fact]
    public async Task AnUnknownId_IsNull()
    {
        var f = new Fixture();

        Assert.Null(await f.Service.OpenAsync(Guid.NewGuid(), thumbnail: false));
        Assert.Null(await f.Service.GetMetadataAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AFileThatIsGoneFromTheStorage_IsNull_AndTheFailureIsLogged()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var upload = await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf");
        f.Disk.Objects.Clear();
        f.Logs.Rows.Clear();

        Assert.Null(await f.Service.OpenAsync(upload.File!.Id, thumbnail: false));

        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal(("Download", "Failed"), (log.Action, log.Status));
    }

    [Fact]
    public async Task AReadThatThrows_IsNull_WithASafeLoggedMessage()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.S3);
        var id = (await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf")).File!.Id;
        f.S3.ReadFailure = new IOException($"boom {S3Secret}");
        f.Logs.Rows.Clear();

        Assert.Null(await f.Service.OpenAsync(id, thumbnail: false));

        Assert.DoesNotContain(S3Secret, f.Logs.Rows.Single().ErrorMessage);
    }

    [Fact]
    public async Task AFile_StaysReadable_AfterTheProjectChangesItsProvider_BecauseItRemembersWhereItIs()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var old = (await f.UploadAsync(TestFiles.FakePdf(), name: "old.pdf", contentType: "application/pdf")).File!;

        await f.ActivateAsync("default", ProviderType.S3);   // the project now writes to S3
        var fresh = (await f.UploadAsync(TestFiles.FakePdf(), name: "new.pdf", contentType: "application/pdf")).File!;

        Assert.Equal((ProviderType.LocalDisk, ProviderType.S3), (old.StorageProvider, fresh.StorageProvider));
        await using (var oldRead = await f.Service.OpenAsync(old.Id, thumbnail: false)) Assert.NotNull(oldRead);   // still read from the disk
        await using (var newRead = await f.Service.OpenAsync(fresh.Id, thumbnail: false)) Assert.NotNull(newRead);
        Assert.Empty(f.S3.Deleted);

        var deleted = await f.Service.DeleteAsync(old.Id);
        Assert.True(deleted.Success);
        Assert.Single(f.Disk.Deleted);   // deleted from the disk it lives on, not from S3
        Assert.Empty(f.S3.Deleted);
    }

    [Fact]
    public async Task AFileWhoseProfileIsGone_CannotBeRead_AndSaysSo()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var id = (await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf")).File!.Id;
        f.Configs.Repository.Rows.Clear();
        f.Logs.Rows.Clear();

        Assert.Null(await f.Service.OpenAsync(id, thumbnail: false));
        var delete = await f.Service.DeleteAsync(id);

        Assert.Equal((true, false), (delete.Found, delete.Success));
        Assert.Contains("no longer exists", delete.Error);
        Assert.Single(f.Files.Rows);   // the record stays so the file is not lost track of
    }

    // ------------------------------------------------------------------------------------------------ delete

    [Fact]
    public async Task ADelete_RemovesTheObject_ItsThumbnail_AndTheRecord_AndIsLogged()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        var id = (await f.UploadAsync(TestFiles.FakePng())).File!.Id;
        var key = f.Files.Rows.Single().ObjectKey;
        f.Logs.Rows.Clear();

        var result = await f.Service.DeleteAsync(id);

        Assert.Equal((true, true, (string?)null), (result.Found, result.Success, result.Error));
        Assert.Empty(f.Files.Rows);
        Assert.Empty(f.Disk.Objects);
        Assert.Equal([key, StoredFile.ThumbnailKeyFor(key)], f.Disk.Deleted);
        Assert.Equal([("Delete", "Success")], f.Logs.Rows.Select(l => (l.Action, l.Status)));
    }

    [Fact]
    public async Task AnUnknownIdToDelete_IsNotFound() =>
        Assert.Equal((false, false), ((await new Fixture().Service.DeleteAsync(Guid.NewGuid())) is var r ? (r.Found, r.Success) : default));

    [Fact]
    public async Task WhenTheObjectCannotBeDeleted_TheRecordStays_SoItCanBeTriedAgain()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.S3);
        var id = (await f.UploadAsync(TestFiles.FakePdf(), name: "a.pdf", contentType: "application/pdf")).File!.Id;
        f.S3.DeleteFailure = new IOException($"denied {S3Secret}");
        f.Logs.Rows.Clear();

        var result = await f.Service.DeleteAsync(id);

        Assert.Equal((true, false), (result.Found, result.Success));
        Assert.DoesNotContain(S3Secret, result.Error);
        Assert.Single(f.Files.Rows);
        var log = Assert.Single(f.Logs.Rows);
        Assert.Equal(("Delete", "Failed"), (log.Action, log.Status));
    }

    // ------------------------------------------------------------------------------------------------ list and logs

    [Fact]
    public async Task TheList_IsNewestFirst_PerProject_WithALimitAndAnOffset()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        foreach (var (project, name) in new[] { ("a", "1.pdf"), ("b", "2.pdf"), ("a", "3.pdf"), ("a", "4.pdf") })
        {
            f.Configs.Clock.Advance(TimeSpan.FromMinutes(1));
            await f.UploadAsync(TestFiles.FakePdf(), project: project, name: name, contentType: "application/pdf");
        }

        Assert.Equal(["4.pdf", "3.pdf", "2.pdf", "1.pdf"], (await f.Service.ListAsync(null, 50, 0)).Select(x => x.OriginalFileName));
        Assert.Equal(["4.pdf", "3.pdf", "1.pdf"], (await f.Service.ListAsync("A", 50, 0)).Select(x => x.OriginalFileName));   // the project is normalised
        Assert.Equal(["3.pdf"], (await f.Service.ListAsync("a", 1, 1)).Select(x => x.OriginalFileName));
        Assert.Single(await f.Service.ListAsync(null, 0, 0));               // the limit is at least 1
        Assert.Equal(4, (await f.Service.ListAsync(null, 100000, -5)).Count);   // and at most 200; a negative offset is 0
    }

    [Fact]
    public async Task TheLogs_AreNewestFirst_PerProject_AndHoldNoPathOrContent()
    {
        var f = new Fixture();
        await f.ActivateAsync("default", ProviderType.LocalDisk);
        await f.UploadAsync(TestFiles.FakePng(), project: "a", name: "secret-plans.png");
        f.Configs.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.UploadAsync(TestFiles.FakePdf(), project: "b", name: "x.pdf", contentType: "application/pdf");

        var all = await f.Service.RecentLogsAsync(null, 20);
        var onlyA = await f.Service.RecentLogsAsync("a", 20);

        Assert.Equal("b", all[0].ProjectCode);
        Assert.All(onlyA, l => Assert.Equal("a", l.ProjectCode));
        var text = string.Join('|', all.Select(l => $"{l.Action}{l.Status}{l.ErrorMessage}"));
        Assert.DoesNotContain("secret-plans", text);
        Assert.DoesNotContain("uploads/", text);
        Assert.Single(await f.Service.RecentLogsAsync(null, 0));
    }

    [Fact]
    public async Task ABadRequest_ThrowsBeforeAnythingIsLooked_At()
    {
        var f = new Fixture();

        Assert.Contains("ProjectCode", (await Assert.ThrowsAsync<ValidationException>(() => f.Service.UploadAsync(new UploadFileCommand("  ", "a.png", null, new MemoryStream([1]))))).Errors.Keys);
        Assert.Contains("FileName", (await Assert.ThrowsAsync<ValidationException>(() => f.Service.UploadAsync(new UploadFileCommand("p", " ", null, new MemoryStream([1]))))).Errors.Keys);
        Assert.Contains("FileName", (await Assert.ThrowsAsync<ValidationException>(() => f.Service.UploadAsync(new UploadFileCommand("p", new string('x', 501), null, new MemoryStream([1]))))).Errors.Keys);
        Assert.Empty(f.Logs.Rows);
    }

    private sealed class EndlessStream(byte[] header) : Stream
    {
        private int _position;

        public long BytesGiven { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();   // a length is not available, and not trusted
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = _position < header.Length ? header[_position++] : (byte)7;
            BytesGiven += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var span = buffer.Span;
            for (var i = 0; i < span.Length; i++) span[i] = _position < header.Length ? header[_position++] : (byte)7;
            BytesGiven += span.Length;
            return ValueTask.FromResult(span.Length);
        }
    }
}

public class StorageTestServiceTests
{
    private const string S3Secret = "fake-secret-access-key-for-tests-0123456789";
    private const string S3Settings = """{"bucket":"media-bucket","region":"us-east-1","accessKeyId":"test-access-key-id"}""";

    private sealed class Fixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeStorageProvider Disk { get; } = new(ProviderType.LocalDisk);
        public FakeStorageProvider S3 { get; } = new(ProviderType.S3);
        public FakeStorageProvider Mock { get; } = new(ProviderType.Mock);
        public StorageTestService Service { get; }

        public Fixture() => Service = new StorageTestService(Configs.Manager, [Disk, S3, Mock], Configs.Audit, Configs.UnitOfWork, NullLogger<StorageTestService>.Instance);

        public Task<ChannelConfigurationDto> SaveAsync(ProviderType provider, string settings, string? secret = null, ChannelType channel = ChannelType.Storage) =>
            Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("default", channel, provider, "Storage", settings, secret));
    }

    [Fact]
    public async Task AnUnsavedConfiguration_IsTriedFromMemory_WithTheSecretFromTheRequest()
    {
        var f = new Fixture();

        var result = await f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.S3, SettingsJson: S3Settings, Secret: S3Secret));

        Assert.True(result.Success);
        Assert.False(result.UsedSavedConfiguration);
        Assert.Null(result.ConfigurationId);
        var config = Assert.Single(f.S3.Tested);
        Assert.Equal(S3Secret, config.Secret);
        Assert.Equal("media-bucket", config.Settings.GetProperty("bucket").GetString());
    }

    [Fact]
    public async Task AnUnsavedTest_CreatesNothing_ChangesNothing_AndOnlyLeavesAnAuditEntry()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.S3, S3Settings, S3Secret);
        await f.Configs.Manager.ActivateAsync(saved.Id);
        var snapshot = f.Configs.Repository.Rows.Select(r => (r.Id, r.Version, r.Status, r.SettingsJson, r.EncryptedSecret, r.LastTestedAt, r.LastTestStatus)).ToList();
        var auditBefore = f.Configs.Audit.Entries.Count;

        await f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.LocalDisk, SettingsJson: """{"rootPath":"elsewhere"}"""));

        Assert.Equal(snapshot, f.Configs.Repository.Rows.Select(r => (r.Id, r.Version, r.Status, r.SettingsJson, r.EncryptedSecret, r.LastTestedAt, r.LastTestStatus)).ToList());
        var audit = Assert.Single(f.Configs.Audit.Entries.Skip(auditBefore));
        Assert.Equal("SendTest", audit.Action);
        Assert.Contains("\"Saved\":false", audit.DetailsJson);
        Assert.DoesNotContain("elsewhere", audit.DetailsJson);
    }

    [Fact]
    public async Task ALocalDiskAndAMockNeedNoSecret()
    {
        var f = new Fixture();

        Assert.True((await f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.LocalDisk, SettingsJson: """{"rootPath":"uploads"}"""))).Success);
        Assert.True((await f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.Mock))).Success);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("••••••••")]
    public async Task S3_NeedsItsSecretInTheRequest_AndAStoredOneIsNotBorrowed(string? secret)
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.S3, S3Settings, S3Secret);
        await f.Configs.Manager.ActivateAsync(saved.Id);

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.S3, SettingsJson: S3Settings, Secret: secret)));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.Empty(f.S3.Tested);
    }

    [Fact]
    public async Task IncompleteSettings_AreRefused_BeforeAnythingIsTried()
    {
        var f = new Fixture();

        var disk = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.LocalDisk)));
        var s3 = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.S3, SettingsJson: """{"bucket":"media-bucket"}""", Secret: S3Secret)));

        Assert.Contains(disk.Errors["SettingsJson"], e => e.Contains("rootPath is required"));
        Assert.Contains(s3.Errors["SettingsJson"], e => e.Contains("accessKeyId is required"));
        Assert.Empty(f.Disk.Tested);
        Assert.Empty(f.S3.Tested);
    }

    [Theory]
    [InlineData("""{"rootPath":"../x"}""")]
    [InlineData("""{"rootPath":"a","secretAccessKey":"oops"}""")]
    public async Task BadSettings_AreRefused(string settings)
    {
        var f = new Fixture();

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.LocalDisk, SettingsJson: settings)));

        Assert.Contains("SettingsJson", error.Errors.Keys);
        Assert.Empty(f.Disk.Tested);
    }

    [Fact]
    public async Task ASecretOfTheWrongShape_IsRefused_WithoutRepeatingIt()
    {
        var f = new Fixture();

        var error = await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.S3, SettingsJson: S3Settings, Secret: "short")));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.DoesNotContain("short", string.Join(' ', error.Errors["Secret"]));
    }

    [Fact]
    public async Task NoProvider_AndAProviderThatIsNotAStorageProvider_AreRefused()
    {
        var f = new Fixture();

        Assert.Contains("ProviderType", (await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestStorageCommand()))).Errors.Keys);
        Assert.Contains("ProviderType", (await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.Smtp)))).Errors.Keys);
    }

    [Fact]
    public async Task AFailedTest_IsAnAnswer_WithTheSecretRemovedFromTheMessage()
    {
        var f = new Fixture();
        f.S3.TestResult = new StorageTestOutcome(false, $"refused {S3Secret}", 30);

        var result = await f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.S3, SettingsJson: S3Settings, Secret: S3Secret));

        Assert.False(result.Success);
        Assert.DoesNotContain(S3Secret, result.Message);
        Assert.Equal(30, result.LatencyMs);
    }

    [Fact]
    public async Task AProviderThatThrows_DuringATest_IsAFailedTest()
    {
        var f = new Fixture();
        f.S3.TestResult = null!;   // makes the fake throw a NullReferenceException when asked

        var result = await f.Service.TestAsync(new TestStorageCommand(ProviderType: ProviderType.S3, SettingsJson: S3Settings, Secret: S3Secret));

        Assert.False(result.Success);
        Assert.DoesNotContain(S3Secret, result.Message);
    }

    [Fact]
    public async Task ASavedVersion_IsTestedWithItsStoredSecret_AndTheResultIsRecorded()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.S3, S3Settings, S3Secret);   // a draft can be tested before it is activated
        f.Configs.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await f.Service.TestAsync(new TestStorageCommand(ConfigurationId: saved.Id));

        Assert.True(result.UsedSavedConfiguration);
        Assert.Equal(saved.Id, result.ConfigurationId);
        Assert.Equal(S3Secret, Assert.Single(f.S3.Tested).Secret);
        var row = f.Configs.Repository.Rows.Single();
        Assert.Equal((TestStatus.Success, f.Configs.Clock.GetUtcNow()), (row.LastTestStatus, row.LastTestedAt));
        Assert.Equal(ConfigurationStatus.Draft, row.Status);   // testing does not activate
    }

    [Fact]
    public async Task AFailedSavedTest_IsRecordedAsFailed()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.S3, S3Settings, S3Secret);
        f.S3.TestResult = new StorageTestOutcome(false, "no", 4);

        var result = await f.Service.TestAsync(new TestStorageCommand(ConfigurationId: saved.Id));

        Assert.False(result.Success);
        Assert.Equal(TestStatus.Failed, f.Configs.Repository.Rows.Single().LastTestStatus);
    }

    [Fact]
    public async Task ASavedTest_IgnoresAnySettingsInTheSameRequest_SoTheStoredSecretGoesOnlyWhereItWasSaved()
    {
        var f = new Fixture();
        var saved = await f.SaveAsync(ProviderType.S3, S3Settings, S3Secret);

        await f.Service.TestAsync(new TestStorageCommand(saved.Id, ProviderType.S3, """{"bucket":"another-bucket","accessKeyId":"x-key-id","region":"us-east-1"}""", S3Secret));

        Assert.Equal("media-bucket", Assert.Single(f.S3.Tested).Settings.GetProperty("bucket").GetString());
    }

    [Fact]
    public async Task AnUnknownId_IsNotFound_AndAnEmailConfigurationIsRefused()
    {
        var f = new Fixture();
        var email = await f.SaveAsync(ProviderType.Mock, "{}", channel: ChannelType.Email);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.TestAsync(new TestStorageCommand(ConfigurationId: Guid.NewGuid())));
        await Assert.ThrowsAsync<ValidationException>(() => f.Service.TestAsync(new TestStorageCommand(ConfigurationId: email.Id)));
    }
}

public class StorageConfigurationRulesTests
{
    [Fact]
    public async Task ChangingFromLocalDiskToS3_WithoutASecret_IsRefused_AndWithOne_Succeeds()
    {
        var fixture = new ManagerFixture();
        await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Storage, ProviderType.LocalDisk, "Storage", """{"rootPath":"u"}"""));

        var error = await Assert.ThrowsAsync<ValidationException>(() => fixture.Manager.SaveAsync(
            new SaveChannelConfigurationCommand("p", ChannelType.Storage, ProviderType.S3, "Storage", """{"bucket":"media-bucket"}""")));
        var ok = await fixture.Manager.SaveAsync(
            new SaveChannelConfigurationCommand("p", ChannelType.Storage, ProviderType.S3, "Storage", """{"bucket":"media-bucket"}""", "fake-secret-access-key-for-tests-0123456789"));

        Assert.Contains("Secret", error.Errors.Keys);
        Assert.Equal(ProviderType.S3, ok.ProviderType);
        Assert.True(ok.HasSecret);
    }

    [Fact]
    public async Task TheS3Secret_IsStoredOnlyProtected_AndNeverShown()
    {
        var fixture = new ManagerFixture();
        const string secret = "fake-secret-access-key-for-tests-0123456789";

        var dto = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Storage, ProviderType.S3, "Storage", """{"bucket":"media-bucket"}""", secret));

        var row = Assert.Single(fixture.Repository.Rows);
        Assert.DoesNotContain(secret, row.EncryptedSecret);
        Assert.DoesNotContain(secret, row.SettingsJson);
        Assert.Equal("••••••••", dto.MaskedSecret);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(dto));
        Assert.All(fixture.Audit.Entries, e => Assert.DoesNotContain(secret, e.DetailsJson));
    }

    [Fact]
    public async Task TheMockProvider_CanServeStorage_WithoutASecret()
    {
        var fixture = new ManagerFixture();

        var dto = await fixture.Manager.SaveAsync(new SaveChannelConfigurationCommand("p", ChannelType.Storage, ProviderType.Mock, "Storage", "{}"));

        Assert.False(dto.HasSecret);
    }
}

public class StorageClientSdkTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? ContentType, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content?.Headers.ContentType?.ToString(), request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static (SharedServicesClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new SharedServicesClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") }), handler);
    }

    private const string FileJson = """{"id":"7f2d6a1e-0000-0000-0000-000000000009","projectCode":"default","storageProvider":"LocalDisk","originalFileName":"a.png","mimeType":"image/png","fileSizeBytes":4,"fileHashSha256":"abc","isPublic":false,"hasThumbnail":true,"createdAt":"2026-11-11T08:00:00+00:00","url":"/api/v1/storage/files/7f2d6a1e-0000-0000-0000-000000000009","thumbnailUrl":"/api/v1/storage/files/7f2d6a1e-0000-0000-0000-000000000009/thumbnail"}""";

    [Fact]
    public async Task Upload_SendsAMultipartForm_WithTheFileItsClaimedType_AndTheFlags()
    {
        var (client, handler) = Make(_ => Json(System.Net.HttpStatusCode.Created, "{\"file\":" + FileJson + ",\"url\":\"/api/v1/storage/files/x\",\"thumbnailUrl\":\"/t\",\"publicUrl\":null}"));

        var response = await client.UploadFileAsync(new UploadFileRequest("a.png", "image/png", IsPublic: true, ProjectCode: "linguabridge"), new MemoryStream([1, 2, 3, 4]));

        Assert.Equal("a.png", response.File.OriginalFileName);
        Assert.True(response.File.HasThumbnail);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(("/api/v1/storage/upload", HttpMethod.Post), (request.Url, request.Method));
        Assert.StartsWith("multipart/form-data", request.ContentType);
        var text = System.Text.Encoding.Latin1.GetString(request.Body);
        Assert.Contains("name=file; filename=a.png", text);
        Assert.Contains("Content-Type: image/png", text);
        Assert.Contains("name=isPublic", text);
        Assert.Contains("name=projectCode", text);
        Assert.Contains("linguabridge", text);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadRequest, 400)]
    [InlineData(System.Net.HttpStatusCode.Conflict, 409)]
    [InlineData(System.Net.HttpStatusCode.RequestEntityTooLarge, 413)]
    [InlineData(System.Net.HttpStatusCode.BadGateway, 502)]
    public async Task Upload_ThatTheServiceRefuses_ThrowsWithTheStatusAndTheDetail(System.Net.HttpStatusCode status, int code)
    {
        var (client, _) = Make(_ => Json(status, """{"title":"x","detail":"The file is not allowed."}"""));

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.UploadFileAsync(new UploadFileRequest("a.png"), new MemoryStream([1])));

        Assert.Equal(code, exception.StatusCode);
        Assert.Contains("not allowed", exception.Message);
    }

    [Fact]
    public async Task Download_StreamsTheBody_WithTheTypeNameAndETag()
    {
        var (client, handler) = Make(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("inline") { FileName = "a.png" };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"");
            return response;
        });

        await using var file = await client.DownloadFileAsync(Guid.Parse("7f2d6a1e-0000-0000-0000-000000000009"));

        Assert.Equal(("image/png", "a.png", "\"abc\"", 3L), (file.ContentType, file.FileName, file.ETag, file.Length));
        using var copy = new MemoryStream();
        await file.Content.CopyToAsync(copy);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        Assert.Equal("/api/v1/storage/files/7f2d6a1e-0000-0000-0000-000000000009", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Download_OfAMissingFile_Throws404_AndAThumbnailOfOneIsNull()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.DownloadFileAsync(Guid.NewGuid()));

        Assert.Equal(404, exception.StatusCode);
        Assert.Null(await client.GetThumbnailAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Download_OfAFileTheKeyMayNotRead_ThrowsWithTheStatus()
    {
        var (client, _) = Make(_ => Json(System.Net.HttpStatusCode.Forbidden, """{"title":"Not your project","detail":"This file belongs to another project."}"""));

        var exception = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.DownloadFileAsync(Guid.NewGuid()));

        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public async Task Metadata_List_Delete_AndMediaLogs_UseTheirEndpoints()
    {
        var (client, handler) = Make(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)
            : request.RequestUri!.AbsolutePath.EndsWith("media-logs")
                ? Json(System.Net.HttpStatusCode.OK, """[{"id":"7f2d6a1e-0000-0000-0000-000000000001","projectCode":"default","fileId":null,"action":"Upload","status":"Success","errorMessage":null,"createdAt":"2026-11-11T08:00:00+00:00"}]""")
                : request.RequestUri.AbsolutePath.EndsWith("metadata")
                    ? Json(System.Net.HttpStatusCode.OK, FileJson)
                    : Json(System.Net.HttpStatusCode.OK, "[" + FileJson + "]"));
        var id = Guid.Parse("7f2d6a1e-0000-0000-0000-000000000009");

        Assert.Equal("a.png", (await client.GetFileMetadataAsync(id))!.OriginalFileName);
        Assert.Single(await client.ListFilesAsync("default", 5));
        Assert.True(await client.DeleteFileAsync(id));
        Assert.Equal("Upload", Assert.Single(await client.GetMediaLogsAsync("default", 7)).Action);

        Assert.Equal(
            [$"/api/v1/storage/files/{id}/metadata", "/api/v1/storage/files?limit=5&projectCode=default", $"/api/v1/storage/files/{id}", "/api/v1/storage/media-logs?limit=7&projectCode=default"],
            handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task MissingFiles_AreNullAndFalse_NotExceptions()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        Assert.Null(await client.GetFileMetadataAsync(Guid.NewGuid()));
        Assert.False(await client.DeleteFileAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task TestStorage_PostsTheRequest_WithTheSecretAsAWriteOnlyField()
    {
        var (client, handler) = Make(_ => Json(System.Net.HttpStatusCode.OK, """{"success":false,"message":"The secret access key is wrong.","latencyMs":80,"providerType":"S3","usedSavedConfiguration":false,"configurationId":null}"""));
        using var settings = System.Text.Json.JsonDocument.Parse("""{"bucket":"media-bucket"}""");

        var response = await client.TestStorageAsync(new StorageTestRequest(ProviderType: "S3", Settings: settings.RootElement.Clone(), Secret: "typed-secret"));

        Assert.False(response.Success);
        Assert.Contains("secret access key", response.Message);
        Assert.Equal("/api/v1/storage/test", handler.Requests[0].Url);
        var body = System.Text.Encoding.UTF8.GetString(handler.Requests[0].Body);
        Assert.Contains("\"secret\":\"typed-secret\"", body);
        Assert.Contains("\"providerType\":\"S3\"", body);
    }
}
