using System.Security.Cryptography;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Storage;

/// <summary>
/// Keeps and serves the files of the projects. An upload goes through, in this order: the project's active storage configuration (or the
/// "default" project's) is found; the bytes are read into memory with the size limit enforced as they arrive (the declared length is not
/// trusted) while the SHA-256 is computed; the real type is read from the first bytes and must be allowed and fit what the client claimed;
/// an image is turned into a thumbnail (which refuses oversized images before decoding them); the file and its thumbnail are written with
/// a server-made key; only then are the record and the media log saved. Reading and deleting use the storage the file was written to,
/// not the one that is active now. Every upload, download, thumbnail and delete is written to the media log.
/// </summary>
public sealed class StorageService(
    IChannelConfigManager configurations,
    IEnumerable<IStorageProvider> providers,
    IImageThumbnailService thumbnails,
    IStoredFileRepository files,
    IMediaLogRepository mediaLogs,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<StorageService> logger,
    IQuotaGuard? quotas = null,
    IEventPublisher? events = null) : IStorageService
{
    public const string FallbackProject = "default";
    private const int MaxRecent = 100;
    private const int MaxList = 200;
    private const int MaxNameLength = 500;

    public async Task<UploadFileResult> UploadAsync(UploadFileCommand command, CancellationToken cancellationToken = default)
    {
        var project = ChannelConfigurationValidator.NormalizeProjectCode(command.ProjectCode);
        var errors = new Dictionary<string, string[]>();
        if (project.Length == 0) errors["ProjectCode"] = ["The project is required."];
        if (string.IsNullOrWhiteSpace(command.FileName)) errors["FileName"] = ["The file name is required."];
        else if (command.FileName.Length > MaxNameLength) errors["FileName"] = [$"The file name is longer than {MaxNameLength} characters."];
        if (errors.Count > 0) throw new ValidationException(errors);

        ChannelRuntimeConfiguration? configuration;
        try
        {
            configuration = await ResolveActive(project, cancellationToken);
        }
        catch (SecretProtectionException)
        {
            return await Failed(project, StorageFailure.ProviderFailure, "The stored storage credentials cannot be read; enter them again.", cancellationToken);
        }

        if (configuration is null)
        {
            return await Failed(project, StorageFailure.NoConfiguration, project == FallbackProject
                ? "There is no active storage configuration."
                : $"Project '{project}' has no active storage configuration, and neither has '{FallbackProject}'.", cancellationToken);
        }

        var provider = providers.FirstOrDefault(p => p.Type == configuration.ProviderType);
        if (provider is null)
            return await Failed(project, StorageFailure.ProviderFailure, $"The {configuration.ProviderType} storage provider is not available.", cancellationToken);

        var settings = StorageSettingsReader.Read(configuration.Settings);
        var problems = StorageSettingsReader.Validate(configuration.ProviderType, configuration.Settings, complete: true);
        if (problems.Count > 0)
            return await Failed(project, StorageFailure.ProviderFailure, "The storage configuration is incomplete: " + string.Join(" ", problems), cancellationToken);

        // Is there room at all? Asked before any byte is read, so a project over its quota does not make the service receive the file first.
        if (quotas is not null) await quotas.EnsureCanStoreAsync(project, 0, cancellationToken);

        // 1. The bytes, with the limit enforced as they arrive, and the hash.
        var read = await ReadBounded(command.Content, settings.MaxFileSizeBytes, cancellationToken);
        if (read.TooLarge)
            return await Failed(project, StorageFailure.TooLarge, $"The file is larger than {settings.MaxFileSizeBytes / 1024 / 1024} MB.", cancellationToken);
        if (read.Length == 0) throw await Refused(project, "Content", "The file is empty.", cancellationToken);
        if (quotas is not null) await quotas.EnsureCanStoreAsync(project, read.Length, cancellationToken);

        // 2. What it really is.
        var mime = MagicBytes.Detect(read.Content.Span[..Math.Min(read.Content.Length, MagicBytes.HeaderLength)]);
        if (mime is null)
            throw await Refused(project, "Content", "This kind of file is not allowed. Images (PNG, JPEG, WebP, GIF), PDF and audio (MP3, OGG, WAV) are accepted.", cancellationToken);
        if (!MagicBytes.DeclaredFits(command.ContentType, mime))
            throw await Refused(project, "ContentType", $"The file is {mime}, not what its Content-Type says.", cancellationToken);

        // 3. The thumbnail (this also rejects an image that is too large in pixels, or is not an image at all, before anything is stored).
        var file = StoredFile.Create(project, configuration.Id, configuration.ProviderType, command.FileName, mime, read.Length, read.Sha256, command.IsPublic, clock.GetUtcNow());
        byte[]? thumbnail = null;
        if (file.CanHaveThumbnail)
        {
            try
            {
                thumbnail = await thumbnails.CreateThumbnailAsync(read.Content, mime, cancellationToken);
            }
            catch (ImageRejectedException ex)
            {
                throw await Refused(project, "Content", ex.Message, cancellationToken);
            }
        }

        // 4. Write, then record. A file whose record could not be saved is taken back out.
        var written = new List<string>();
        try
        {
            await provider.PutAsync(configuration, file.ObjectKey, AsStream(read.Content), mime, cancellationToken);
            written.Add(file.ObjectKey);
            if (thumbnail is not null)
            {
                await provider.PutAsync(configuration, file.ThumbnailObjectKey, new MemoryStream(thumbnail, writable: false), "image/webp", cancellationToken);
                written.Add(file.ThumbnailObjectKey);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Cleanup(provider, configuration, written);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {Provider} storage provider could not write a file for project {Project}.", configuration.ProviderType, project);
            await Cleanup(provider, configuration, written);
            return await Failed(project, StorageFailure.ProviderFailure, ErrorText.Clean(ex.Message, configuration.Secret), cancellationToken);
        }

        try
        {
            files.Add(file);
            mediaLogs.Add(MediaLog.Create(project, file.Id, "Upload", "Success", null, clock.GetUtcNow()));
            if (thumbnail is not null) mediaLogs.Add(MediaLog.Create(project, file.Id, "Thumbnail", "Success", null, clock.GetUtcNow()));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await Cleanup(provider, configuration, written);
            throw;
        }

        if (events is not null)
            await events.PublishAsync(WebhookEvents.FileUploaded, project, new { fileId = file.Id, fileName = file.OriginalFileName, mimeType = file.MimeType, sizeBytes = file.FileSizeBytes, isPublic = file.IsPublic }, cancellationToken);

        var publicUrl = file.IsPublic && settings.PublicUrlBase is { Length: > 0 } baseUrl ? $"{baseUrl.TrimEnd('/')}/{file.ObjectKey}" : null;
        return new UploadFileResult(true, StorageFailure.None, ToDto(file), null, publicUrl);
    }

    public async Task<StoredFileDto?> GetMetadataAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await files.GetByIdAsync(id, cancellationToken);
        return file is null ? null : ToDto(file);
    }

    public async Task<DownloadedFile?> OpenAsync(Guid id, bool thumbnail, bool logDownload = true, CancellationToken cancellationToken = default)
    {
        var file = await files.GetByIdAsync(id, cancellationToken);
        if (file is null) return null;
        if (thumbnail && !file.CanHaveThumbnail) return null;

        var (provider, configuration, problem) = await ResolveFor(file, cancellationToken);
        if (provider is null || configuration is null)
        {
            if (!thumbnail) await LogAsync(file.ProjectCode, file.Id, "Download", "Failed", problem, cancellationToken);
            return null;
        }

        var key = thumbnail ? file.ThumbnailObjectKey : file.ObjectKey;
        try
        {
            var stream = await provider.OpenReadAsync(configuration, key, cancellationToken);
            if (stream is null)
            {
                if (!thumbnail) await LogAsync(file.ProjectCode, file.Id, "Download", "Failed", "The file is no longer in the storage.", cancellationToken);
                return null;
            }

            // A thumbnail is only a preview (a gallery asks for many), so only real downloads are logged.
            if (!thumbnail && logDownload) await LogAsync(file.ProjectCode, file.Id, "Download", "Success", null, cancellationToken);
            var length = thumbnail ? (stream.CanSeek ? stream.Length : (long?)null) : file.FileSizeBytes;
            return new DownloadedFile(ToDto(file), stream, thumbnail ? "image/webp" : file.MimeType, length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {Provider} storage provider could not read file {FileId}.", configuration.ProviderType, file.Id);
            if (!thumbnail) await LogAsync(file.ProjectCode, file.Id, "Download", "Failed", ErrorText.Clean(ex.Message, configuration.Secret), cancellationToken);
            return null;
        }
    }

    public async Task<IReadOnlyList<StoredFileDto>> ListAsync(string? projectCode, int limit, int offset, CancellationToken cancellationToken = default)
    {
        var project = string.IsNullOrWhiteSpace(projectCode) ? null : ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        var rows = await files.ListAsync(project, Math.Clamp(limit, 1, MaxList), Math.Max(offset, 0), cancellationToken);
        return [.. rows.Select(ToDto)];
    }

    public async Task<DeleteFileResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var file = await files.GetByIdAsync(id, cancellationToken);
        if (file is null) return new DeleteFileResult(false, false, null);

        var (provider, configuration, problem) = await ResolveFor(file, cancellationToken);
        if (provider is null || configuration is null)
        {
            await LogAsync(file.ProjectCode, file.Id, "Delete", "Failed", problem, cancellationToken);
            return new DeleteFileResult(true, false, problem);
        }

        try
        {
            await provider.DeleteAsync(configuration, file.ObjectKey, cancellationToken);
            if (file.CanHaveThumbnail) await provider.DeleteAsync(configuration, file.ThumbnailObjectKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The record stays while the object may still exist, so the file can be deleted again later.
            logger.LogWarning(ex, "The {Provider} storage provider could not delete file {FileId}.", configuration.ProviderType, file.Id);
            var message = ErrorText.Clean(ex.Message, configuration.Secret);
            await LogAsync(file.ProjectCode, file.Id, "Delete", "Failed", message, cancellationToken);
            return new DeleteFileResult(true, false, message);
        }

        // The log row is written first and points at the file; the database clears that pointer when the record goes (the log outlives the file).
        mediaLogs.Add(MediaLog.Create(file.ProjectCode, file.Id, "Delete", "Success", null, clock.GetUtcNow()));
        files.Remove(file);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DeleteFileResult(true, true, null);
    }

    public async Task<IReadOnlyList<MediaLogDto>> RecentLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default)
    {
        var project = string.IsNullOrWhiteSpace(projectCode) ? null : ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        var rows = await mediaLogs.ListRecentAsync(project, Math.Clamp(limit, 1, MaxRecent), cancellationToken);
        return [.. rows.Select(l => new MediaLogDto(l.Id, l.ProjectCode, l.FileId, l.Action, l.Status, l.ErrorMessage, l.CreatedAt))];
    }

    // ------------------------------------------------------------------------------------------------ steps

    private static StoredFileDto ToDto(StoredFile f) =>
        new(f.Id, f.ProjectCode, f.StorageProvider, f.OriginalFileName, f.MimeType, f.FileSizeBytes, f.FileHashSha256, f.IsPublic, f.CanHaveThumbnail, f.CreatedAt);

    /// <summary>The project's own active storage configuration, then the default project's.</summary>
    private async Task<ChannelRuntimeConfiguration?> ResolveActive(string project, CancellationToken cancellationToken)
    {
        var own = await configurations.GetActiveForRuntimeAsync(project, ChannelType.Storage, cancellationToken: cancellationToken);
        if (own is not null || project == FallbackProject) return own;
        return await configurations.GetActiveForRuntimeAsync(FallbackProject, ChannelType.Storage, cancellationToken: cancellationToken);
    }

    /// <summary>The provider and configuration a file was written with (not the ones that are active now).</summary>
    private async Task<(IStorageProvider? Provider, ChannelRuntimeConfiguration? Configuration, string? Problem)> ResolveFor(StoredFile file, CancellationToken cancellationToken)
    {
        ChannelRuntimeConfiguration? configuration;
        try
        {
            configuration = await configurations.GetForRuntimeAsync(file.StorageProfileId, cancellationToken);
        }
        catch (SecretProtectionException)
        {
            return (null, null, "The stored storage credentials cannot be read; enter them again.");
        }

        if (configuration is null) return (null, null, "The storage configuration the file was written with no longer exists.");
        var provider = providers.FirstOrDefault(p => p.Type == file.StorageProvider);
        return provider is null ? (null, null, $"The {file.StorageProvider} storage provider is not available.") : (provider, configuration, null);
    }

    /// <summary>The bytes as a read-only stream, without copying them again.</summary>
    private static MemoryStream AsStream(ReadOnlyMemory<byte> bytes) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);

    private sealed record BoundedRead(ReadOnlyMemory<byte> Content, int Length, string Sha256, bool TooLarge);

    private static async Task<BoundedRead> ReadBounded(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes) return new BoundedRead(ReadOnlyMemory<byte>.Empty, 0, string.Empty, TooLarge: true);
            hash.AppendData(chunk, 0, read);
            buffer.Write(chunk, 0, read);
        }

        return new BoundedRead(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), (int)buffer.Length, Convert.ToHexStringLower(hash.GetHashAndReset()), TooLarge: false);
    }

    private async Task<UploadFileResult> Failed(string project, StorageFailure failure, string error, CancellationToken cancellationToken)
    {
        await LogAsync(project, null, "Upload", "Failed", error, cancellationToken);
        return new UploadFileResult(false, failure, null, error);
    }

    /// <summary>Logs a refused upload and returns the exception to throw.</summary>
    private async Task<ValidationException> Refused(string project, string field, string message, CancellationToken cancellationToken)
    {
        await LogAsync(project, null, "Upload", "Failed", message, cancellationToken);
        return new ValidationException(new Dictionary<string, string[]> { [field] = [message] });
    }

    private async Task LogAsync(string project, Guid? fileId, string action, string status, string? error, CancellationToken cancellationToken)
    {
        mediaLogs.Add(MediaLog.Create(project, fileId, action, status, error is null ? null : ErrorText.Clean(error), clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task Cleanup(IStorageProvider provider, ChannelRuntimeConfiguration configuration, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await provider.DeleteAsync(configuration, key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not take back an object after a failed upload.");
            }
        }
    }
}
