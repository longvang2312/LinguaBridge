using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Storage;

/// <summary>
/// The non-secret part of a storage configuration (the keys of <c>SettingsJson</c>): <c>rootPath, publicUrlBase, maxFileSizeMb</c> for a
/// folder on the server; <c>bucket, region, serviceUrl, forcePathStyle, accessKeyId, publicUrlBase, maxFileSizeMb</c> for S3.
/// The S3 secret access key is the secret and is kept apart.
/// </summary>
public sealed record StorageSettings(
    string? RootPath, string? PublicUrlBase, int? MaxFileSizeMb, string? Bucket, string? Region, string? ServiceUrl, bool ForcePathStyle, string? AccessKeyId)
{
    public const int DefaultMaxFileSizeMb = 15;

    /// <summary>The upper limit whatever the settings say: a file is held in memory while it is checked.</summary>
    public const int HardMaxFileSizeMb = 50;

    public long MaxFileSizeBytes => (long)Math.Clamp(MaxFileSizeMb ?? DefaultMaxFileSizeMb, 1, HardMaxFileSizeMb) * 1024 * 1024;
}

public static partial class StorageSettingsReader
{
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketName();

    [GeneratedRegex("^[a-z0-9-]{2,30}$")]
    private static partial Regex RegionName();

    [GeneratedRegex(@"^[A-Za-z0-9_+=/.-]{4,128}$")]
    private static partial Regex AccessKey();

    private static readonly string[] Keys = ["rootPath", "publicUrlBase", "maxFileSizeMb", "bucket", "region", "serviceUrl", "forcePathStyle", "accessKeyId"];

    public static StorageSettings Read(JsonElement settings)
    {
        string? Text(string name) =>
            settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;

        int? maxMb = null;
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("maxFileSizeMb", out var mb) && mb.ValueKind == JsonValueKind.Number && mb.TryGetInt32(out var parsed))
            maxMb = parsed;

        var pathStyle = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("forcePathStyle", out var fps) && fps.ValueKind == JsonValueKind.True;
        return new StorageSettings(Text("rootPath"), Text("publicUrlBase"), maxMb, Text("bucket"), Text("region"), Text("serviceUrl"), pathStyle, Text("accessKeyId"));
    }

    /// <summary>
    /// The problems with these settings. With <paramref name="complete"/> false (saving a draft) only the shape of what is given is checked;
    /// with it true (a test or an upload) what the provider cannot work without must be there too.
    /// </summary>
    public static IReadOnlyList<string> Validate(ProviderType provider, JsonElement settings, bool complete)
    {
        if (settings.ValueKind != JsonValueKind.Object) return ["The settings must be a JSON object."];

        var problems = new List<string>();
        foreach (var name in Keys.Where(k => k is not ("maxFileSizeMb" or "forcePathStyle")))
        {
            if (settings.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                problems.Add($"{name} must be text.");
        }

        if (settings.TryGetProperty("maxFileSizeMb", out var mb) && mb.ValueKind != JsonValueKind.Null
            && !(mb.ValueKind == JsonValueKind.Number && mb.TryGetInt32(out var n) && n is >= 1 and <= StorageSettings.HardMaxFileSizeMb))
            problems.Add($"maxFileSizeMb must be a whole number from 1 to {StorageSettings.HardMaxFileSizeMb}.");

        if (settings.TryGetProperty("forcePathStyle", out var fps) && fps.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            problems.Add("forcePathStyle must be true or false.");

        if (problems.Count > 0) return problems;

        var read = Read(settings);
        if (read.RootPath is { Length: > 0 } root && RootPathProblem(root) is { } rootProblem) problems.Add(rootProblem);
        if (read.PublicUrlBase is { Length: > 0 } publicBase && !IsHttpAddress(publicBase, allowPath: true)) problems.Add("publicUrlBase must be an http or https address without a login or query.");
        if (read.ServiceUrl is { Length: > 0 } service && !IsHttpAddress(service, allowPath: false)) problems.Add("serviceUrl must be an http or https address (host and port only, no login, path or query).");
        if (read.Bucket is { Length: > 0 } bucket && !BucketName().IsMatch(bucket)) problems.Add("bucket must be 3 to 63 lower-case letters, digits, '.' or '-'.");
        if (read.Region is { Length: > 0 } region && !RegionName().IsMatch(region)) problems.Add("region must look like us-east-1.");
        if (read.AccessKeyId is { Length: > 0 } key && !AccessKey().IsMatch(key)) problems.Add("accessKeyId has characters an access key id does not use.");

        if (complete)
        {
            switch (provider)
            {
                case ProviderType.LocalDisk when string.IsNullOrEmpty(read.RootPath):
                    problems.Add("rootPath is required (the folder files are kept in).");
                    break;
                case ProviderType.S3:
                    if (string.IsNullOrEmpty(read.Bucket)) problems.Add("bucket is required.");
                    if (string.IsNullOrEmpty(read.AccessKeyId)) problems.Add("accessKeyId is required.");
                    if (string.IsNullOrEmpty(read.Region) && string.IsNullOrEmpty(read.ServiceUrl)) problems.Add("Give a region (AWS) or a serviceUrl (MinIO, R2).");
                    break;
            }
        }

        return problems;
    }

    /// <summary>A folder name: no control characters, no ".." part (it must stay under the folder the host allows), not too long.</summary>
    private static string? RootPathProblem(string root)
    {
        if (root.Length > 260 || root.Any(char.IsControl)) return "rootPath must be at most 260 characters, on one line.";
        if (root.Split('/', '\\').Any(part => part == "..")) return "rootPath must not contain '..'.";
        return null;
    }

    private static bool IsHttpAddress(string value, bool allowPath)
    {
        if (value.Length > 300 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        return allowPath || uri.AbsolutePath == "/";
    }
}

/// <summary>Checks the secret of a storage configuration: the S3 secret access key. Messages never repeat the value.</summary>
public static class StorageSecretRules
{
    public static string? Check(ProviderType provider, string secret)
    {
        if (provider != ProviderType.S3) return null;
        if (secret.Length is < 8 or > 128 || secret.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return "The secret access key must be 8 to 128 characters with no spaces.";
        return null;
    }
}
