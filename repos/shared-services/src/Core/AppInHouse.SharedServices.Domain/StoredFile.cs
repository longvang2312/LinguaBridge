namespace AppInHouse.SharedServices.Domain;

/// <summary>
/// The record of a file kept in some storage profile. The object key is made by the server (a random id), never taken from the
/// name the client sent, so a client cannot choose where a file lands (no path traversal).
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

    public static StoredFile Create(
        string projectCode, Guid storageProfileId, ProviderType storageProvider, string originalFileName, string mimeType,
        long fileSizeBytes, string fileHashSha256, bool isPublic, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(projectCode)) throw new DomainException("A stored file belongs to a project.");
        if (fileSizeBytes < 0) throw new DomainException("A file cannot have a negative size.");
        if (storageProvider is not (ProviderType.LocalDisk or ProviderType.S3))
            throw new DomainException($"The {storageProvider} provider is not a storage provider.");

        var id = Guid.NewGuid();
        return new StoredFile
        {
            Id = id,
            ProjectCode = projectCode,
            StorageProfileId = storageProfileId,
            StorageProvider = storageProvider,
            ObjectKey = $"{projectCode}/{id:N}",
            OriginalFileName = Path.GetFileName(originalFileName),
            MimeType = mimeType,
            FileSizeBytes = fileSizeBytes,
            FileHashSha256 = fileHashSha256,
            IsPublic = isPublic,
            CreatedAt = now,
        };
    }
}
