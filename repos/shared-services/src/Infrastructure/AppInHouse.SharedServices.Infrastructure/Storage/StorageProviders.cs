using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Email;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AppInHouse.SharedServices.Infrastructure.Storage;

/// <summary>Where the storage providers get their host settings from (section <c>Storage</c>). None of it is stored per project.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// The folder every relative <c>rootPath</c> of a local-disk configuration is placed under. A configuration can therefore not make the
    /// service write outside it (a path with ".." is refused, and the resolved path is checked again).
    /// </summary>
    public string BasePath { get; set; } = "data/storage";

    /// <summary>Whether a local-disk configuration may name an absolute folder anywhere on the server. Off by default.</summary>
    public bool AllowAbsolutePaths { get; set; }

    /// <summary>
    /// Whether an S3 <c>serviceUrl</c> may point at a loopback or private network (a MinIO next to the service). Off by default, and independent of
    /// <c>Email:AllowPrivateNetworkHosts</c>: allowing storage on the internal network does not open mail to it. Link-local and metadata addresses are never allowed.
    /// </summary>
    public bool AllowPrivateNetworkHosts { get; set; }

    public int S3TimeoutSeconds { get; set; } = 30;
}

/// <summary>A storage problem whose message is safe to show (no secret, no server path).</summary>
public sealed class StorageProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Files kept in memory, for development and tests. Nothing survives a restart.</summary>
public sealed class MockStorageProvider : IStorageProvider
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public ProviderType Type => ProviderType.Mock;

    public int Count => _objects.Count;

    public bool Contains(string objectKey) => _objects.ContainsKey(objectKey);

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys];

    public async Task PutAsync(ChannelRuntimeConfiguration configuration, string objectKey, Stream content, string mimeType, CancellationToken cancellationToken = default)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        _objects[objectKey] = copy.ToArray();
    }

    public Task<Stream?> OpenReadAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(_objects.TryGetValue(objectKey, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

    public Task DeleteAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default)
    {
        _objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }

    public Task<StorageTestOutcome> TestConnectionAsync(ChannelRuntimeConfiguration configuration, CancellationToken cancellationToken = default) =>
        Task.FromResult(new StorageTestOutcome(true, "The mock storage works (files are kept in memory and lost on restart).", 0));
}

/// <summary>
/// Files in a folder on the server. Settings: <c>rootPath</c> (placed under the host's base folder unless the host allows absolute paths),
/// <c>maxFileSizeMb</c>, <c>publicUrlBase</c>. Whatever key it is given, the path it ends up with is checked to be inside the root.
/// </summary>
public sealed class LocalDiskStorageProvider(IOptions<StorageOptions> options) : IStorageProvider
{
    public ProviderType Type => ProviderType.LocalDisk;

    public async Task PutAsync(ChannelRuntimeConfiguration configuration, string objectKey, Stream content, string mimeType, CancellationToken cancellationToken = default)
    {
        var path = PathFor(Root(configuration), objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // CreateNew: an object is written once and never overwritten (the key is a fresh random id).
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default)
    {
        var path = PathFor(Root(configuration), objectKey);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }

    public Task DeleteAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default)
    {
        File.Delete(PathFor(Root(configuration), objectKey));   // a file that is already gone is fine
        return Task.CompletedTask;
    }

    public async Task<StorageTestOutcome> TestConnectionAsync(ChannelRuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string Elapsed() => $"{stopwatch.ElapsedMilliseconds} ms";
        var shown = StorageSettingsReader.Read(configuration.Settings).RootPath;

        string? canary = null;
        IReadOnlyList<string> created = [];
        try
        {
            var root = Root(configuration);
            created = MissingFolders(root);   // what the test is about to create, so it can take it away again
            Directory.CreateDirectory(root);
            canary = Path.Combine(root, $".canary-{Guid.NewGuid():N}.tmp");

            await File.WriteAllBytesAsync(canary, [0x42], cancellationToken);
            var back = await File.ReadAllBytesAsync(canary, cancellationToken);
            File.Delete(canary);
            canary = null;

            return back is [0x42]
                ? new StorageTestOutcome(true, $"The folder '{shown}' can be written and read ({Elapsed()}).", (int)stopwatch.ElapsedMilliseconds)
                : new StorageTestOutcome(false, $"The folder '{shown}' did not give back what was written.", (int)stopwatch.ElapsedMilliseconds);
        }
        catch (StorageProviderException ex)
        {
            return new StorageTestOutcome(false, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageTestOutcome(false, $"The service has no permission to write in the folder '{shown}'.", (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return new StorageTestOutcome(false, $"The folder '{shown}' cannot be used (is the path valid, and is there room?).", (int)stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            try { if (canary is not null) File.Delete(canary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            RemoveEmpty(created);
        }
    }

    /// <summary>The folders from <paramref name="root"/> upwards that do not exist yet, deepest first (the order to remove them in).</summary>
    private static List<string> MissingFolders(string root)
    {
        var missing = new List<string>();
        for (var folder = Path.TrimEndingDirectorySeparator(root); !string.IsNullOrEmpty(folder) && !Directory.Exists(folder); folder = Path.GetDirectoryName(folder))
            missing.Add(folder);
        return missing;
    }

    /// <summary>Removes the folders a test created, but only while they are empty: a folder that has files in it is never touched.</summary>
    private static void RemoveEmpty(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover empty folder is not a failure of the test.
            }
        }
    }

    /// <summary>The folder files of this configuration live in. A relative path goes under the host's base folder; an absolute one only if the host allows it.</summary>
    private string Root(ChannelRuntimeConfiguration configuration)
    {
        var configured = StorageSettingsReader.Read(configuration.Settings).RootPath;
        if (string.IsNullOrWhiteSpace(configured)) throw new StorageProviderException("The configuration has no rootPath.");

        var baseFolder = Path.GetFullPath(options.Value.BasePath);
        if (Path.IsPathRooted(configured))
        {
            if (!options.Value.AllowAbsolutePaths) throw new StorageProviderException("An absolute rootPath is not allowed on this server; use a folder name relative to the storage base folder.");
            return Path.GetFullPath(configured);
        }

        var root = Path.GetFullPath(Path.Combine(baseFolder, configured));
        if (!IsInside(baseFolder, root)) throw new StorageProviderException("The rootPath leads outside the storage base folder.");
        return root;
    }

    /// <summary>The file for an object key: the key may not be absolute or contain "..", and the full path must be inside the root.</summary>
    internal static string PathFor(string root, string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || Path.IsPathRooted(objectKey) || objectKey.Contains('\\') || objectKey.Split('/').Any(part => part is ".." or "." or ""))
            throw new StorageProviderException("The object key is not valid.");

        var full = Path.GetFullPath(Path.Combine(root, objectKey));
        if (!IsInside(root, full)) throw new StorageProviderException("The object key leads outside the storage folder.");
        return full;
    }

    private static bool IsInside(string folder, string path)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>Makes the S3 client for one call. A test can supply its own HTTP layer.</summary>
public interface IS3ClientFactory
{
    IAmazonS3 Create(StorageSettings settings, string secretAccessKey);
}

public sealed class S3ClientFactory(IOptions<StorageOptions> options, HttpClientFactory? httpClientFactory = null) : IS3ClientFactory
{
    public IAmazonS3 Create(StorageSettings settings, string secretAccessKey)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
            MaxErrorRetry = 1,
            Timeout = TimeSpan.FromSeconds(options.Value.S3TimeoutSeconds),
            // Only compute and check checksums where the protocol asks for it: MinIO and R2 do not all accept the SDK's newer defaults.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        if (!string.IsNullOrEmpty(settings.ServiceUrl))
        {
            config.ServiceURL = settings.ServiceUrl;
            config.AuthenticationRegion = string.IsNullOrEmpty(settings.Region) ? "us-east-1" : settings.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }

        if (httpClientFactory is not null) config.HttpClientFactory = httpClientFactory;
        return new AmazonS3Client(new BasicAWSCredentials(settings.AccessKeyId, secretAccessKey), config);
    }
}

/// <summary>
/// Files in an S3-compatible bucket (AWS S3, MinIO, Cloudflare R2). Settings: <c>bucket, region, serviceUrl, forcePathStyle, accessKeyId</c>;
/// the secret access key is the configuration's secret. A <c>serviceUrl</c> is an address the service is asked to contact, so its host is
/// checked like an SMTP host (never a link-local or metadata address; a private network only when the host allows it).
/// </summary>
public sealed class S3StorageProvider(IS3ClientFactory clients, OutboundHostPolicy hostPolicy, ILogger<S3StorageProvider> logger) : IStorageProvider
{
    public ProviderType Type => ProviderType.S3;

    public async Task PutAsync(ChannelRuntimeConfiguration configuration, string objectKey, Stream content, string mimeType, CancellationToken cancellationToken = default)
    {
        var (client, bucket) = await OpenClient(configuration, cancellationToken);
        using (client)
        {
            // The SDK signs the body, so it must be able to read it twice: keep a seekable copy.
            var body = content.CanSeek ? content : await Buffer(content, cancellationToken);
            try
            {
                await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = objectKey, InputStream = body, ContentType = mimeType, AutoCloseStream = false }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Describe(ex, "write the file");
            }
        }
    }

    public async Task<Stream?> OpenReadAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default)
    {
        var (client, bucket) = await OpenClient(configuration, cancellationToken);
        using (client)
        {
            try
            {
                using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = objectKey }, cancellationToken);
                return await Buffer(response.ResponseStream, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Describe(ex, "read the file");
            }
        }
    }

    public async Task DeleteAsync(ChannelRuntimeConfiguration configuration, string objectKey, CancellationToken cancellationToken = default)
    {
        var (client, bucket) = await OpenClient(configuration, cancellationToken);
        using (client)
        {
            try
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = objectKey }, cancellationToken);   // S3 does not complain about a missing key
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Describe(ex, "delete the file");
            }
        }
    }

    public async Task<StorageTestOutcome> TestConnectionAsync(ChannelRuntimeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int Latency() => (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        string? canaryKey = null;
        IAmazonS3? client = null;
        var bucket = string.Empty;
        try
        {
            (client, bucket) = await OpenClient(configuration, cancellationToken);

            // 1. The bucket exists and the key may list it.
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, cancellationToken);

            // 2. A one-byte object can be written, read back and deleted.
            canaryKey = $".canary/{Guid.NewGuid():N}";
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = canaryKey, InputStream = new MemoryStream([0x42]), ContentType = "application/octet-stream", AutoCloseStream = true }, cancellationToken);
            using (var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = canaryKey }, cancellationToken))
            {
                var back = await Buffer(response.ResponseStream, cancellationToken);
                if (back.Length != 1) return new StorageTestOutcome(false, $"The bucket '{bucket}' did not give back what was written.", Latency());
            }

            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = canaryKey }, cancellationToken);
            canaryKey = null;
            return new StorageTestOutcome(true, $"The bucket '{bucket}' can be listed, written and read ({stopwatch.ElapsedMilliseconds} ms).", Latency());
        }
        catch (StorageProviderException ex)
        {
            return new StorageTestOutcome(false, ex.Message, Latency());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The S3 storage test failed.");
            return new StorageTestOutcome(false, Describe(ex, "use the bucket").Message, Latency());
        }
        finally
        {
            if (client is not null)
            {
                try { if (canaryKey is not null) await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = canaryKey }, CancellationToken.None); } catch (Exception) { }
                client.Dispose();
            }
        }
    }

    private async Task<(IAmazonS3 Client, string Bucket)> OpenClient(ChannelRuntimeConfiguration configuration, CancellationToken cancellationToken)
    {
        var problems = StorageSettingsReader.Validate(ProviderType.S3, configuration.Settings, complete: true);
        if (problems.Count > 0) throw new StorageProviderException("The S3 settings are incomplete: " + string.Join(" ", problems));
        if (string.IsNullOrEmpty(configuration.Secret)) throw new StorageProviderException("The secret access key is missing.");

        var settings = StorageSettingsReader.Read(configuration.Settings);
        if (!string.IsNullOrEmpty(settings.ServiceUrl))
        {
            // Check every host the SDK will really connect to. Without path style it connects to "{bucket}.{host}" (virtual-hosted style),
            // a name the owner of a wildcard DNS record can point anywhere (the metadata address, a private network), so that name is
            // checked too. An IP address has no bucket sub-domain: the SDK uses path style for it.
            var host = new Uri(settings.ServiceUrl).IdnHost;
            var hosts = new List<string> { host };
            if (!settings.ForcePathStyle && !System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _)) hosts.Add($"{settings.Bucket}.{host}");
            foreach (var name in hosts)
            {
                if (await hostPolicy.CheckAsync(name, cancellationToken) is { } blocked) throw new StorageProviderException(blocked);
            }
        }

        return (clients.Create(settings, configuration.Secret), settings.Bucket!);
    }

    private static async Task<MemoryStream> Buffer(Stream source, CancellationToken cancellationToken)
    {
        var copy = new MemoryStream();
        await source.CopyToAsync(copy, cancellationToken);
        copy.Position = 0;
        return copy;
    }

    /// <summary>A short, safe explanation of what S3 (or the network) said. The SDK's own text can repeat request details, so it is not passed on.</summary>
    private static StorageProviderException Describe(Exception ex, string doing)
    {
        if (ex is AmazonS3Exception s3)
        {
            var reason = s3.ErrorCode switch
            {
                "InvalidAccessKeyId" => "The access key id is not known to the service.",
                "SignatureDoesNotMatch" => "The secret access key is wrong.",
                "NoSuchBucket" => "The bucket does not exist.",
                "AccessDenied" => "The key is not allowed to do this on the bucket.",
                "AuthorizationHeaderMalformed" or "InvalidRegion" => "The region does not match the bucket or service.",
                _ => null,
            };
            return new StorageProviderException($"Could not {doing}: " + (reason ?? $"the service answered {(int)s3.StatusCode} {s3.ErrorCode}."), ex);
        }

        return new StorageProviderException($"Could not {doing}: the service cannot be reached or did not answer in time.", ex);
    }
}
