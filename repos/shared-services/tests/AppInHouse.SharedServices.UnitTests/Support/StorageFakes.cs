using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Domain;
using SkiaSharp;

namespace AppInHouse.SharedServices.UnitTests.Support;

/// <summary>A storage provider in memory that records what it was asked and fails when told to.</summary>
internal sealed class FakeStorageProvider(ProviderType type) : IStorageProvider
{
    public ProviderType Type => type;

    public Dictionary<string, byte[]> Objects { get; } = [];
    public List<(ChannelRuntimeConfiguration Config, string Key, string Mime)> Puts { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<ChannelRuntimeConfiguration> Tested { get; } = [];

    public Exception? PutFailure { get; set; }
    public Exception? DeleteFailure { get; set; }
    public Exception? ReadFailure { get; set; }

    /// <summary>Fail only the write of a thumbnail (the file itself is written first).</summary>
    public bool FailThumbnailPut { get; set; }

    public StorageTestOutcome TestResult { get; set; } = new(true, "Storage works.", 3);

    public async Task PutAsync(ChannelRuntimeConfiguration configuration, string objectKey, Stream content, string mimeType, CancellationToken cancellationToken = default)
    {
        if (PutFailure is not null || (FailThumbnailPut && objectKey.EndsWith("_thumb.webp", StringComparison.Ordinal)))
            throw PutFailure ?? new IOException("disk full");
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        Objects[objectKey] = copy.ToArray();
        Puts.Add((configuration, objectKey, mimeType));
    }

    public Task<Stream?> OpenReadAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default)
    {
        if (ReadFailure is not null) throw ReadFailure;
        return Task.FromResult<Stream?>(Objects.TryGetValue(objectKey, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
    }

    public Task DeleteAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default)
    {
        if (DeleteFailure is not null) throw DeleteFailure;
        Objects.Remove(objectKey);
        Deleted.Add(objectKey);
        return Task.CompletedTask;
    }

    public Task<StorageTestOutcome> TestConnectionAsync(ChannelRuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Tested.Add(configuration);
        return Task.FromResult(TestResult);
    }
}

internal sealed class FakeStoredFileRepository : IStoredFileRepository
{
    public List<StoredFile> Rows { get; } = [];

    public void Add(StoredFile file) => Rows.Add(file);

    public Task<StoredFile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Rows.FirstOrDefault(f => f.Id == id));

    public void Remove(StoredFile file) => Rows.Remove(file);

    public Task<IReadOnlyList<StoredFile>> ListAsync(string? projectCode, int limit, int offset, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoredFile>>([.. Rows.Where(f => projectCode is null || f.ProjectCode == projectCode).OrderByDescending(f => f.CreatedAt).Skip(offset).Take(limit)]);
}

internal sealed class FakeMediaLogRepository : IMediaLogRepository
{
    public List<MediaLog> Rows { get; } = [];

    public void Add(MediaLog log) => Rows.Add(log);

    public Task<IReadOnlyList<MediaLog>> ListRecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MediaLog>>([.. Rows.Where(l => projectCode is null || l.ProjectCode == projectCode).OrderByDescending(l => l.CreatedAt).Take(limit)]);
}

/// <summary>A unit of work whose save can be made to fail, to see what is taken back.</summary>
internal sealed class ThrowingUnitOfWork : IUnitOfWork
{
    public Exception? Failure { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Failure is null ? Task.FromResult(1) : throw Failure;
}

/// <summary>A thumbnail service that answers as told and counts its calls.</summary>
internal sealed class FakeThumbnails : IImageThumbnailService
{
    public byte[] Result { get; set; } = [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50];
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }

    public Task<byte[]> CreateThumbnailAsync(ReadOnlyMemory<byte> image, string mimeType, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Failure is null ? Task.FromResult(Result) : throw Failure;
    }
}

/// <summary>Bytes that start like each kind of file (and real images, made with SkiaSharp).</summary>
internal static class TestFiles
{
    public static byte[] WithHeader(byte[] header, int total = 200)
    {
        var bytes = new byte[Math.Max(total, header.Length)];
        header.CopyTo(bytes, 0);
        for (var i = header.Length; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    public static byte[] FakePng(int total = 200) => WithHeader([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], total);
    public static byte[] FakeJpeg(int total = 200) => WithHeader([0xFF, 0xD8, 0xFF, 0xE0], total);
    public static byte[] FakePdf(int total = 200) => WithHeader("%PDF-1.7\n"u8.ToArray(), total);
    public static byte[] FakeGif(int total = 200) => WithHeader("GIF89a"u8.ToArray(), total);
    public static byte[] FakeMp3(int total = 200) => WithHeader("ID3\u0003\u0000"u8.ToArray(), total);
    public static byte[] FakeOgg(int total = 200) => WithHeader("OggS\0\u0002"u8.ToArray(), total);
    public static byte[] FakeWav(int total = 200) => WithHeader([.. "RIFF"u8, 0x24, 0, 0, 0, .. "WAVEfmt "u8], total);
    public static byte[] FakeWebp(int total = 200) => WithHeader([.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8], total);

    /// <summary>A Windows executable (starts with MZ) that a client might call image/png.</summary>
    public static byte[] Executable(int total = 200) => WithHeader("MZ\u0090\0\u0003\0\0\0"u8.ToArray(), total);

    public static byte[] Script() => "#!/bin/sh\nrm -rf /\n"u8.ToArray();

    public static byte[] Real(SKEncodedImageFormat format, int width, int height, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color ?? SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    public static byte[] RealPng(int width = 600, int height = 400) => Real(SKEncodedImageFormat.Png, width, height);
    public static byte[] RealJpeg(int width = 600, int height = 400) => Real(SKEncodedImageFormat.Jpeg, width, height);
    public static byte[] RealWebp(int width = 600, int height = 400) => Real(SKEncodedImageFormat.Webp, width, height);
}
