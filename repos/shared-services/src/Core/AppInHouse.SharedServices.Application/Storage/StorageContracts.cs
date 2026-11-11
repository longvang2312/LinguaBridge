using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Storage;

/// <summary>What one storage test found: it worked or not, in words that are safe to show (no secret access key), and how long it took.</summary>
public sealed record StorageTestOutcome(bool Success, string Message, int LatencyMs);

/// <summary>
/// One place files can be kept (a folder on the server, an S3-compatible bucket, memory for development). It is handed the configuration
/// to use on every call, so activating a new version takes effect at once. The object key is made by the server, never by a caller.
/// A provider reports an operational failure by throwing, with a message that contains no secret.
/// </summary>
public interface IStorageProvider
{
    ProviderType Type { get; }

    Task PutAsync(ChannelRuntimeConfiguration configuration, string objectKey, Stream content, string mimeType, CancellationToken cancellationToken = default);

    /// <summary>The content, or null when there is no such object. The caller disposes the stream.</summary>
    Task<Stream?> OpenReadAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object; an object that is already gone is not an error.</summary>
    Task DeleteAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Writes a one-byte canary object, reads it back and deletes it (and checks the folder or bucket). Never throws for a configuration problem: it says so in the outcome.</summary>
    Task<StorageTestOutcome> TestConnectionAsync(ChannelRuntimeConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>The thumbnail service refused the image (too large in pixels, or not a readable image).</summary>
public sealed class ImageRejectedException(string message) : Exception(message);

/// <summary>Limits that keep a small file from being turned into a huge amount of memory (a "decompression bomb").</summary>
public static class ImageLimits
{
    /// <summary>Largest width or height, in pixels.</summary>
    public const int MaxDimension = 8000;

    /// <summary>Largest width x height, in pixels (the square of the dimension limit would be 64 million).</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>The longer side of a thumbnail.</summary>
    public const int ThumbnailSize = 256;
}

public interface IImageThumbnailService
{
    /// <summary>
    /// A WebP thumbnail (the longer side at most <see cref="ImageLimits.ThumbnailSize"/> pixels) of a PNG, JPEG or WebP image. The size is read
    /// from the header first and the image is refused, before it is decoded, when it is larger than <see cref="ImageLimits"/> allows.
    /// </summary>
    /// <exception cref="ImageRejectedException">Too large, or not a readable image.</exception>
    Task<byte[]> CreateThumbnailAsync(ReadOnlyMemory<byte> image, string mimeType, CancellationToken cancellationToken = default);
}

/// <summary>What the storage service knows about a file. The object key (where it is kept) is not part of it.</summary>
public sealed record StoredFileDto(
    Guid Id, string ProjectCode, ProviderType StorageProvider, string OriginalFileName, string MimeType, long FileSizeBytes,
    string FileHashSha256, bool IsPublic, bool HasThumbnail, DateTimeOffset CreatedAt)
{
    /// <summary>Where the content is read, relative to the API (the same for every project).</summary>
    public string Url => $"/api/v1/storage/files/{Id}";

    /// <summary>Where the thumbnail is read; null for a file that has none.</summary>
    public string? ThumbnailUrl => HasThumbnail ? $"/api/v1/storage/files/{Id}/thumbnail" : null;
}

public sealed record MediaLogDto(Guid Id, string ProjectCode, Guid? FileId, string Action, string Status, string? ErrorMessage, DateTimeOffset CreatedAt);

/// <summary>Upload one file for a project. <see cref="ContentType"/> is only what the client claims: the real type is read from the bytes.</summary>
public sealed record UploadFileCommand(string ProjectCode, string FileName, string? ContentType, Stream Content, bool IsPublic = false);

public enum StorageFailure
{
    None,

    /// <summary>The project has no active storage configuration, and neither has "default".</summary>
    NoConfiguration,

    /// <summary>The file is larger than the configuration's limit.</summary>
    TooLarge,

    /// <summary>The storage could not take the file (folder not writable, bucket refused, ...).</summary>
    ProviderFailure,
}

public sealed record UploadFileResult(bool Success, StorageFailure Failure, StoredFileDto? File, string? Error, string? PublicUrl = null);

/// <summary>A file's content to stream to a caller. The caller disposes it.</summary>
public sealed class DownloadedFile(StoredFileDto file, Stream content, string contentType, long? length) : IAsyncDisposable
{
    public StoredFileDto File { get; } = file;
    public Stream Content { get; } = content;
    public string ContentType { get; } = contentType;
    public long? Length { get; } = length;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record DeleteFileResult(bool Found, bool Success, string? Error);

public interface IStorageService
{
    /// <exception cref="Common.ValidationException">An empty file, a type that is not allowed or does not match what the bytes are, an image that is too large in pixels.</exception>
    Task<UploadFileResult> UploadAsync(UploadFileCommand command, CancellationToken cancellationToken = default);

    Task<StoredFileDto?> GetMetadataAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The content (or the thumbnail) of a file, read from where it was stored. Null when the file or its thumbnail does not exist. A download is logged.</summary>
    /// <param name="logDownload">
    /// Whether a successful read is written to the media log. A caller that serves byte ranges passes false for the ranges that are only
    /// the continuation of a download that was already logged. A failed read is always logged.
    /// </param>
    Task<DownloadedFile?> OpenAsync(Guid id, bool thumbnail, bool logDownload = true, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredFileDto>> ListAsync(string? projectCode, int limit, int offset, CancellationToken cancellationToken = default);

    Task<DeleteFileResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaLogDto>> RecentLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default);
}

public interface IStoredFileRepository
{
    /// <summary>Adds the row to the current unit of work.</summary>
    void Add(StoredFile file);

    /// <summary>Tracked, so it can be removed by the unit of work.</summary>
    Task<StoredFile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Remove(StoredFile file);

    /// <summary>For reading only; the newest first. A null project means every project.</summary>
    Task<IReadOnlyList<StoredFile>> ListAsync(string? projectCode, int limit, int offset, CancellationToken cancellationToken = default);
}

public interface IMediaLogRepository
{
    void Add(MediaLog log);

    Task<IReadOnlyList<MediaLog>> ListRecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Test a storage configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated.
/// Without it, <see cref="ProviderType"/>, <see cref="SettingsJson"/> and <see cref="Secret"/> (the S3 secret access key) are tried from
/// memory: nothing is stored, nothing is activated.
/// </summary>
public sealed record TestStorageCommand(Guid? ConfigurationId = null, ProviderType? ProviderType = null, string? SettingsJson = null, string? Secret = null);

public sealed record TestStorageResult(bool Success, string Message, int LatencyMs, ProviderType ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

public interface IStorageTestService
{
    Task<TestStorageResult> TestAsync(TestStorageCommand command, CancellationToken cancellationToken = default);
}
