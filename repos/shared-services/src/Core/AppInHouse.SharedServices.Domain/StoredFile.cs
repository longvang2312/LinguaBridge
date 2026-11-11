namespace AppInHouse.SharedServices.Domain;

/// <summary>
/// The record of a file kept in some storage profile. The object key is made by the server
/// (<c>uploads/{project}/{yyyy}/{MM}/{guid}{ext}</c>, with a random id and an extension taken from the detected type), never from the name
/// the client sent, so a client cannot choose where a file lands (no path traversal). <see cref="StorageProvider"/> and
/// <see cref="StorageProfileId"/> say where the file really is, so it can still be read or deleted after the project's active provider changes.
/// </summary>
public sealed class StoredFile
{
    private StoredFile()
    {
        ProjectCode = string.Empty;
        ObjectKey = string.Empty;
        OriginalFileName = string.Empty;
        MimeType = string.Empty;
        FileHashSha256 = string.Empty;
    }

    public Guid Id { get; private set; }
    public string ProjectCode { get; private set; }

    /// <summary>The storage configuration (channel type Storage) the file was written with.</summary>
    public Guid StorageProfileId { get; private set; }

    public ProviderType StorageProvider { get; private set; }
    public string ObjectKey { get; private set; }
    public string OriginalFileName { get; private set; }
    public string MimeType { get; private set; }
    public long FileSizeBytes { get; private set; }
    public string FileHashSha256 { get; private set; }
    public bool IsPublic { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Where the thumbnail of an image is kept: the same key with <c>_thumb.webp</c> instead of the extension.</summary>
    public string ThumbnailObjectKey => ThumbnailKeyFor(ObjectKey);

    /// <summary>Whether a thumbnail is made for this kind of file (the formats the image service reads).</summary>
    public bool CanHaveThumbnail => MimeType is "image/png" or "image/jpeg" or "image/webp";

    public static string ThumbnailKeyFor(string objectKey)
    {
        var dot = objectKey.LastIndexOf('.');
        var slash = objectKey.LastIndexOf('/');
        return (dot > slash ? objectKey[..dot] : objectKey) + "_thumb.webp";
    }

    /// <summary>The extension (with its dot) a stored file gets for a detected MIME type; the one the client sent is never used.</summary>
    public static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "application/pdf" => ".pdf",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/wav" => ".wav",
        _ => ".bin",
    };

    public static StoredFile Create(
        string projectCode, Guid storageProfileId, ProviderType storageProvider, string originalFileName, string mimeType,
        long fileSizeBytes, string fileHashSha256, bool isPublic, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(projectCode)) throw new DomainException("A stored file belongs to a project.");
        if (fileSizeBytes < 0) throw new DomainException("A file cannot have a negative size.");
        if (storageProvider is not (ProviderType.LocalDisk or ProviderType.S3 or ProviderType.Mock))
            throw new DomainException($"The {storageProvider} provider is not a storage provider.");

        var id = Guid.NewGuid();
        var utc = now.UtcDateTime;
        return new StoredFile
        {
            Id = id,
            ProjectCode = projectCode,
            StorageProfileId = storageProfileId,
            StorageProvider = storageProvider,
            ObjectKey = $"uploads/{projectCode}/{utc:yyyy}/{utc:MM}/{id:N}{ExtensionFor(mimeType)}",
            OriginalFileName = SafeFileName(originalFileName),
            MimeType = mimeType,
            FileSizeBytes = fileSizeBytes,
            FileHashSha256 = fileHashSha256,
            IsPublic = isPublic,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// The name a client sent, cleaned for display and for the download header: both kinds of separator count, so "../../etc/passwd" and
    /// a Windows path with ".." parts become the bare last name; control characters are dropped; at most 255 characters. Never used as a path.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        var text = (name ?? string.Empty).Replace('\\', '/');
        text = text[(text.LastIndexOf('/') + 1)..];
        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim().Trim('.');
        if (clean.Length == 0) clean = "file";
        return clean.Length <= 255 ? clean : clean[^255..];
    }
}
