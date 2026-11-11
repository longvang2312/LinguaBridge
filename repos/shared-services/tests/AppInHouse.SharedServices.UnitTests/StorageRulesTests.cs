using System.Text.Json;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Storage;
using AppInHouse.SharedServices.UnitTests.Support;

namespace AppInHouse.SharedServices.UnitTests;

public class MagicBytesTests
{
    [Theory]
    [MemberData(nameof(Genuine))]
    public void AGenuineFile_IsRecognisedByItsBytes(string expected, byte[] bytes) =>
        Assert.Equal(expected, MagicBytes.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, MagicBytes.HeaderLength))));

    public static TheoryData<string, byte[]> Genuine() => new()
    {
        { "image/png", TestFiles.FakePng() },
        { "image/jpeg", TestFiles.FakeJpeg() },
        { "image/gif", TestFiles.FakeGif() },
        { "image/webp", TestFiles.FakeWebp() },
        { "application/pdf", TestFiles.FakePdf() },
        { "audio/mpeg", TestFiles.FakeMp3() },
        { "audio/ogg", TestFiles.FakeOgg() },
        { "audio/wav", TestFiles.FakeWav() },
        { "image/png", TestFiles.RealPng(8, 8) },
        { "image/jpeg", TestFiles.RealJpeg(8, 8) },
        { "image/webp", TestFiles.RealWebp(8, 8) },
    };

    [Fact]
    public void AnMp3WithoutATag_IsRecognisedByItsFrameSync()
    {
        Assert.Equal("audio/mpeg", MagicBytes.Detect([0xFF, 0xFB, 0x90, 0x00]));
        Assert.Null(MagicBytes.Detect([0xFF, 0xE0, 0x00, 0x00]));   // layer bits 00 are reserved: not an MP3 frame
    }

    [Fact]
    public void AnExecutable_AScript_AnSvg_AndAnArchive_AreNotAllowedTypes()
    {
        Assert.Null(MagicBytes.Detect(TestFiles.Executable().AsSpan(0, 16)));                     // Windows PE: "MZ"
        Assert.Null(MagicBytes.Detect([0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]));      // Linux ELF
        Assert.Null(MagicBytes.Detect(TestFiles.Script()));                                        // #!/bin/sh
        Assert.Null(MagicBytes.Detect("<svg xmlns='http://www.w3.org/2000/svg'><script>"u8));      // SVG can carry script
        Assert.Null(MagicBytes.Detect("<html><script>alert(1)</script>"u8));
        Assert.Null(MagicBytes.Detect("PK\u0003\u0004zipfile....."u8));                            // zip (also docx, jar, apk)
    }

    [Fact]
    public void ARiffFile_ThatIsNeitherWebpNorWav_IsNotAllowed() =>
        Assert.Null(MagicBytes.Detect([.. "RIFF"u8, 0x24, 0, 0, 0, .. "AVI LIST"u8]));

    [Fact]
    public void AnEmptyOrTooShortHeader_IsNotAType()
    {
        Assert.Null(MagicBytes.Detect([]));
        Assert.Null(MagicBytes.Detect([0xFF]));
        Assert.Null(MagicBytes.Detect([0x89, 0x50, 0x4E]));   // a PNG cut short
    }

    [Theory]
    [InlineData(null, "image/png", true)]
    [InlineData("", "image/png", true)]
    [InlineData("application/octet-stream", "image/png", true)]
    [InlineData("image/png", "image/png", true)]
    [InlineData("IMAGE/PNG; charset=binary", "image/png", true)]
    [InlineData("image/jpg", "image/jpeg", true)]
    [InlineData("audio/mp3", "audio/mpeg", true)]
    [InlineData("audio/x-wav", "audio/wav", true)]
    [InlineData("image/jpeg", "image/png", false)]        // a PNG called a JPEG
    [InlineData("application/pdf", "image/png", false)]
    [InlineData("text/html", "image/png", false)]
    [InlineData("application/x-msdownload", "application/pdf", false)]
    public void WhatTheClientClaims_MustFitWhatTheBytesAre(string? declared, string detected, bool fits) =>
        Assert.Equal(fits, MagicBytes.DeclaredFits(declared, detected));
}

public class StorageSettingsTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void TheSettingsAreRead_WithTheirDefaults()
    {
        var read = StorageSettingsReader.Read(Json("""{"rootPath":" uploads ","maxFileSizeMb":10,"bucket":"b-1","region":"eu-west-1","serviceUrl":"http://minio:9000","forcePathStyle":true,"accessKeyId":"key-id-1","publicUrlBase":"https://cdn.example.com/f"}"""));

        Assert.Equal(("uploads", 10, "b-1", "eu-west-1", "http://minio:9000", true, "key-id-1", "https://cdn.example.com/f"),
            (read.RootPath, read.MaxFileSizeMb, read.Bucket, read.Region, read.ServiceUrl, read.ForcePathStyle, read.AccessKeyId, read.PublicUrlBase));
        Assert.Equal(10L * 1024 * 1024, read.MaxFileSizeBytes);
        Assert.Equal(15L * 1024 * 1024, StorageSettingsReader.Read(Json("{}")).MaxFileSizeBytes);   // the default is 15 MB
        Assert.False(StorageSettingsReader.Read(Json("{}")).ForcePathStyle);
    }

    [Fact]
    public void TheSizeLimit_NeverGoesAboveTheHardCap_OrBelowOneMegabyte()
    {
        Assert.Equal(50L * 1024 * 1024, new StorageSettings(null, null, 5000, null, null, null, false, null).MaxFileSizeBytes);
        Assert.Equal(1L * 1024 * 1024, new StorageSettings(null, null, 0, null, null, null, false, null).MaxFileSizeBytes);
    }

    [Theory]
    [InlineData("""{"rootPath":"uploads","maxFileSizeMb":10,"publicUrlBase":"https://cdn.example.com/files"}""")]
    [InlineData("""{"rootPath":"a/b/c"}""")]
    [InlineData("{}")]
    public void GoodLocalDiskSettings_Pass(string settings) =>
        Assert.Empty(StorageSettingsReader.Validate(ProviderType.LocalDisk, Json(settings), complete: false));

    [Theory]
    [InlineData("""{"rootPath":"../outside"}""", "'..'")]
    [InlineData("""{"rootPath":"a/../../b"}""", "'..'")]
    [InlineData("""{"rootPath":"a\\..\\b"}""", "'..'")]
    [InlineData("""{"rootPath":"a\u0001b"}""", "rootPath")]
    [InlineData("""{"maxFileSizeMb":0}""", "maxFileSizeMb")]
    [InlineData("""{"maxFileSizeMb":51}""", "maxFileSizeMb")]
    [InlineData("""{"maxFileSizeMb":"ten"}""", "maxFileSizeMb")]
    [InlineData("""{"publicUrlBase":"ftp://x"}""", "publicUrlBase")]
    [InlineData("""{"publicUrlBase":"https://user:pw@cdn.example.com"}""", "publicUrlBase")]
    [InlineData("""{"rootPath":5}""", "rootPath")]
    public void BadShapes_AreReported_WhenSavingADraft(string settings, string mentions)
    {
        var problems = StorageSettingsReader.Validate(ProviderType.LocalDisk, Json(settings), complete: false);

        Assert.Contains(problems, p => p.Contains(mentions));
    }

    [Fact]
    public void ARunNeedsTheFolder_ButADraftDoesNot()
    {
        Assert.Empty(StorageSettingsReader.Validate(ProviderType.LocalDisk, Json("{}"), complete: false));
        Assert.Contains(StorageSettingsReader.Validate(ProviderType.LocalDisk, Json("{}"), complete: true), p => p.Contains("rootPath is required"));
    }

    [Fact]
    public void S3_NeedsABucket_AKeyId_AndARegionOrAServiceUrl_ForARun()
    {
        var problems = StorageSettingsReader.Validate(ProviderType.S3, Json("{}"), complete: true);

        Assert.Contains(problems, p => p.Contains("bucket is required"));
        Assert.Contains(problems, p => p.Contains("accessKeyId is required"));
        Assert.Contains(problems, p => p.Contains("region"));
        Assert.Empty(StorageSettingsReader.Validate(ProviderType.S3, Json("""{"bucket":"my-bucket","accessKeyId":"key-id-1","region":"us-east-1"}"""), complete: true));
        Assert.Empty(StorageSettingsReader.Validate(ProviderType.S3, Json("""{"bucket":"my-bucket","accessKeyId":"key-id-1","serviceUrl":"http://minio:9000","forcePathStyle":true}"""), complete: true));
    }

    [Theory]
    [InlineData("""{"bucket":"UPPER"}""", "bucket")]
    [InlineData("""{"bucket":"a"}""", "bucket")]
    [InlineData("""{"bucket":"a b c"}""", "bucket")]
    [InlineData("""{"region":"Not A Region"}""", "region")]
    [InlineData("""{"serviceUrl":"minio:9000"}""", "serviceUrl")]
    [InlineData("""{"serviceUrl":"http://minio:9000/some/path"}""", "serviceUrl")]
    [InlineData("""{"serviceUrl":"http://user:pw@minio:9000"}""", "serviceUrl")]
    [InlineData("""{"serviceUrl":"http://minio:9000?x=1"}""", "serviceUrl")]
    [InlineData("""{"serviceUrl":"file:///etc/passwd"}""", "serviceUrl")]
    [InlineData("""{"accessKeyId":"has space"}""", "accessKeyId")]
    [InlineData("""{"forcePathStyle":"yes"}""", "forcePathStyle")]
    public void BadS3Shapes_AreReported(string settings, string mentions) =>
        Assert.Contains(StorageSettingsReader.Validate(ProviderType.S3, Json(settings), complete: false), p => p.Contains(mentions));

    [Fact]
    public void TheSettingsMustBeAnObject() =>
        Assert.Single(StorageSettingsReader.Validate(ProviderType.LocalDisk, Json("[1,2]"), complete: false));

    [Theory]
    [InlineData("fake-secret-access-key-for-tests-0123456789", true)]
    [InlineData("12345678", true)]
    [InlineData("short", false)]
    [InlineData("has a space in it 1234", false)]
    [InlineData("tab\tinside-1234567", false)]
    public void TheSecretAccessKey_MustLookLikeOne(string secret, bool ok) =>
        Assert.Equal(ok, StorageSecretRules.Check(ProviderType.S3, secret) is null);

    [Fact]
    public void TheSecretMessage_NeverRepeatsTheValue() =>
        Assert.DoesNotContain("short", StorageSecretRules.Check(ProviderType.S3, "short")!);

    [Fact]
    public void OtherProviders_HaveNoStorageSecretRule() =>
        Assert.Null(StorageSecretRules.Check(ProviderType.LocalDisk, "x"));

    // ---- the same rules through the configuration validator (what saving a draft does)

    private static IReadOnlyDictionary<string, string[]> Check(ProviderType provider, string settings, string? secret = null) =>
        ChannelConfigurationValidator.Validate(new SaveChannelConfigurationCommand("p", ChannelType.Storage, provider, "Storage", settings, secret));

    [Fact]
    public void SavingAStorageDraft_ChecksTheSettingsAndTheSecretShape()
    {
        Assert.Empty(Check(ProviderType.LocalDisk, """{"rootPath":"uploads"}"""));
        Assert.Empty(Check(ProviderType.Mock, "{}"));
        Assert.Contains("SettingsJson", Check(ProviderType.LocalDisk, """{"rootPath":"../x"}""").Keys);
        Assert.Contains("Secret", Check(ProviderType.S3, "{}", "short").Keys);
        Assert.Empty(Check(ProviderType.S3, """{"bucket":"my-bucket"}""", "fake-secret-access-key-for-tests-0123456789"));
    }

    [Fact]
    public void ASecretKeyTypedIntoTheSettings_IsStillRefused()
    {
        var errors = Check(ProviderType.S3, """{"bucket":"my-bucket","secretAccessKey":"abc"}""");

        Assert.Contains(errors["SettingsJson"], e => e.Contains("looks like a secret"));
    }

    [Fact]
    public void StorageNowAllowsTheMockProvider()
    {
        Assert.True(ProviderCatalog.Supports(ChannelType.Storage, ProviderType.Mock));
        Assert.False(ProviderCatalog.RequiresSecret(ProviderType.LocalDisk));
        Assert.True(ProviderCatalog.RequiresSecret(ProviderType.S3));
    }
}

public class ImageThumbnailServiceTests
{
    private readonly ImageThumbnailService _service = new();

    private static SkiaSharp.SKCodec Codec(byte[] bytes) => SkiaSharp.SKCodec.Create(SkiaSharp.SKData.CreateCopy(bytes));

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public async Task AnImage_GetsAWebpThumbnail_NoLargerThan256_WithTheSameShape(string mime)
    {
        var source = mime switch { "image/png" => TestFiles.RealPng(1200, 600), "image/jpeg" => TestFiles.RealJpeg(1200, 600), _ => TestFiles.RealWebp(1200, 600) };

        var thumbnail = await _service.CreateThumbnailAsync(source, mime);

        Assert.Equal("image/webp", MagicBytes.Detect(thumbnail.AsSpan(0, 16)));
        using var codec = Codec(thumbnail);
        Assert.Equal((256, 128), (codec.Info.Width, codec.Info.Height));   // 2:1 stays 2:1, the long side is 256
        Assert.True(thumbnail.Length < source.Length || thumbnail.Length < 2000);
    }

    [Fact]
    public async Task ASmallImage_IsNotEnlarged()
    {
        var thumbnail = await _service.CreateThumbnailAsync(TestFiles.RealPng(100, 50), "image/png");

        using var codec = Codec(thumbnail);
        Assert.Equal((100, 50), (codec.Info.Width, codec.Info.Height));
    }

    [Fact]
    public async Task ATallImage_IsFittedByItsHeight()
    {
        var thumbnail = await _service.CreateThumbnailAsync(TestFiles.RealPng(300, 900), "image/png");

        using var codec = Codec(thumbnail);
        Assert.Equal((85, 256), (codec.Info.Width, codec.Info.Height));
    }

    [Fact]
    public async Task AnImageAtTheLimit_IsAccepted_AndOnePixelOverIsRefused()
    {
        // 8000x1 and 8001x1 are tiny files; the limit is on the width and height, not on the bytes.
        var ok = await _service.CreateThumbnailAsync(TestFiles.RealPng(ImageLimits.MaxDimension, 1), "image/png");
        Assert.NotEmpty(ok);

        var tooWide = await Assert.ThrowsAsync<ImageRejectedException>(() => _service.CreateThumbnailAsync(TestFiles.RealPng(ImageLimits.MaxDimension + 1, 1), "image/png"));
        var tooTall = await Assert.ThrowsAsync<ImageRejectedException>(() => _service.CreateThumbnailAsync(TestFiles.RealPng(1, ImageLimits.MaxDimension + 1), "image/png"));

        Assert.Contains("8001", tooWide.Message);
        Assert.Contains("8000", tooTall.Message);
    }

    [Fact]
    public async Task ADecompressionBomb_IsRefusedFromTheHeader_BeforeItsPixelsAreDecoded()
    {
        // A PNG whose header says 60000x60000 (14 GB of pixels) but whose data is a few bytes: only the header is read.
        var bomb = PngHeaderOnly(60000, 60000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<ImageRejectedException>(() => _service.CreateThumbnailAsync(bomb, "image/png"));

        Assert.True(stopwatch.ElapsedMilliseconds < 2000, "the refusal must not wait for a huge decode");
    }

    [Fact]
    public async Task AFileThatIsNotAnImage_IsRefused()
    {
        await Assert.ThrowsAsync<ImageRejectedException>(() => _service.CreateThumbnailAsync(TestFiles.FakePng(), "image/png"));   // PNG signature, garbage inside
        await Assert.ThrowsAsync<ImageRejectedException>(() => _service.CreateThumbnailAsync(new byte[] { 1, 2, 3, 4 }, "image/png"));
        await Assert.ThrowsAsync<ImageRejectedException>(() => _service.CreateThumbnailAsync(ReadOnlyMemory<byte>.Empty, "image/png"));
    }

    [Fact]
    public async Task ATruncatedImage_IsRefused_NotACrash()
    {
        var png = TestFiles.RealPng(400, 400);

        await Assert.ThrowsAsync<ImageRejectedException>(() => _service.CreateThumbnailAsync(png.AsMemory(0, png.Length / 3), "image/png"));
    }

    /// <summary>The 8-byte signature and an IHDR chunk with the given size (the CRC is right, the pixel data is missing).</summary>
    private static byte[] PngHeaderOnly(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // RGBA

        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, ihdr.Length);
        stream.Write(length);
        var typeAndData = new byte[4 + ihdr.Length];
        "IHDR"u8.CopyTo(typeAndData);
        ihdr.CopyTo(typeAndData, 4);
        stream.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
        return stream.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }
}
