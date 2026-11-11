using System.Security.Claims;
using System.Text.Json;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>The answer to an upload: the file, and where to read it.</summary>
public sealed record UploadFileResponse(StoredFileDto File, string Url, string? ThumbnailUrl, string? PublicUrl);

/// <summary>
/// Test a storage configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated. Without
/// it, <see cref="ProviderType"/>, <see cref="Settings"/> and <see cref="Secret"/> (the S3 secret access key) are tried from memory and
/// nothing is stored.
/// </summary>
public sealed record StorageTestRequest(Guid? ConfigurationId = null, ProviderType? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

public sealed record StorageTestResponse(bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

public static class StorageEndpoints
{
    /// <summary>Above the configurable limit (at most 50 MB) so the service itself, not the web server, answers "too large".</summary>
    private const long MaxRequestBytes = 60L * 1024 * 1024;

    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/storage").WithTags("Storage");

        group.MapPost("/upload", async (IFormFile? file, [FromForm] bool? isPublic, [FromForm] string? projectCode, ClaimsPrincipal user, IStorageService storage, CancellationToken ct) =>
            {
                if (file is null || file.Length == 0) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No file", detail: "Send the file as the multipart form field 'file'.");
                if (!ProjectAccess.TryResolve(user, projectCode, out var project)) return Forbidden("An API key can only upload to its own project.");

                await using var content = file.OpenReadStream();
                var result = await storage.UploadAsync(new UploadFileCommand(project ?? ProjectAccess.PlatformProject, file.FileName, file.ContentType, content, isPublic ?? false), ct);

                return result.Failure switch
                {
                    StorageFailure.None => Results.Created(result.File!.Url, new UploadFileResponse(result.File, result.File.Url, result.File.ThumbnailUrl, result.PublicUrl)),
                    StorageFailure.NoConfiguration => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No storage configuration", detail: result.Error),
                    StorageFailure.TooLarge => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The file is too large", detail: result.Error),
                    _ => Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The file could not be stored", detail: result.Error),
                };
            })
            .RequireAuthorization(Policies.StorageUpload)
            .DisableAntiforgery()   // callers use an API key, not a browser session with a cookie
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
            .Accepts<IFormFile>("multipart/form-data")
            .WithName("UploadFile")
            .WithSummary("Stores a file (multipart field 'file', optional 'isPublic' and 'projectCode'). The type is read from the bytes, not from the name or Content-Type.");

        // A public file is read by anyone; a private one needs a key (or token) of its own project with a storage scope. The check is per file, so these are not behind a policy.
        group.MapGet("/files/{id:guid}", (Guid id, bool? download, HttpContext http, ClaimsPrincipal user, IStorageService storage, CancellationToken ct) =>
                ServeAsync(id, thumbnail: false, download ?? false, http, user, storage, ct))
            .AllowAnonymous()
            .WithName("DownloadFile")
            .WithSummary("Streams a file with its real Content-Type, ETag and Cache-Control. Add ?download=true for an attachment.");

        group.MapGet("/files/{id:guid}/thumbnail", (Guid id, HttpContext http, ClaimsPrincipal user, IStorageService storage, CancellationToken ct) =>
                ServeAsync(id, thumbnail: true, download: false, http, user, storage, ct))
            .AllowAnonymous()
            .WithName("GetThumbnail")
            .WithSummary("Streams the WebP thumbnail of an image.");

        group.MapGet("/files/{id:guid}/metadata", async (Guid id, ClaimsPrincipal user, IStorageService storage, CancellationToken ct) =>
            {
                var file = await storage.GetMetadataAsync(id, ct);
                if (file is null) return Results.NotFound();
                return ProjectAccess.TryResolve(user, file.ProjectCode, out _) ? Results.Ok(file) : Forbidden("This file belongs to another project.");
            })
            .RequireAuthorization(Policies.StorageRead)
            .WithName("GetFileMetadata")
            .WithSummary("What is known about a file (no content, no storage path).");

        group.MapGet("/files", async ([FromQuery] string? projectCode, [FromQuery] int? limit, [FromQuery] int? offset, ClaimsPrincipal user, IStorageService storage, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true)) return Forbidden("An API key can only list the files of its own project.");
                return Results.Ok(await storage.ListAsync(project, limit ?? 50, offset ?? 0, ct));
            })
            .RequireAuthorization(Policies.StorageRead)
            .WithName("ListFiles")
            .WithSummary("The files of a project, newest first (limit up to 200, offset for the next page).");

        group.MapDelete("/files/{id:guid}", async (Guid id, ClaimsPrincipal user, IStorageService storage, CancellationToken ct) =>
            {
                var file = await storage.GetMetadataAsync(id, ct);
                if (file is null) return Results.NotFound();
                if (!ProjectAccess.TryResolve(user, file.ProjectCode, out _)) return Forbidden("This file belongs to another project.");

                var result = await storage.DeleteAsync(id, ct);
                if (!result.Found) return Results.NotFound();
                return result.Success
                    ? Results.NoContent()
                    : Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The file could not be deleted", detail: result.Error);
            })
            .RequireAuthorization(Policies.StorageDelete)
            .WithName("DeleteFile")
            .WithSummary("Deletes the file and its thumbnail from the storage it was written to, and its record.");

        group.MapPost("/test", async (StorageTestRequest request, IStorageTestService tests, CancellationToken ct) =>
            {
                var result = await tests.TestAsync(new TestStorageCommand(
                    request.ConfigurationId, request.ProviderType,
                    request.Settings is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } settings ? settings.GetRawText() : null,
                    request.Secret), ct);
                return Results.Ok(new StorageTestResponse(result.Success, result.Message, result.LatencyMs, result.ProviderType.ToString(), result.UsedSavedConfiguration, result.ConfigurationId));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("TestStorage")
            .WithSummary("Writes, reads and deletes a one-byte canary. Without a configuration id the settings in the request are tried from memory and nothing is saved or activated.");

        group.MapGet("/media-logs", async ([FromQuery] string? projectCode, [FromQuery] int? limit, ClaimsPrincipal user, IStorageService storage, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true)) return Forbidden("An API key can only read the activity of its own project.");
                return Results.Ok(await storage.RecentLogsAsync(project, limit ?? 20, ct));
            })
            .RequireAuthorization(Policies.StorageRead)
            .WithName("ListMediaLogs")
            .WithSummary("The most recent uploads, downloads, thumbnails and deletes (no file content, no storage path).");

        return routes;
    }

    private static IResult Forbidden(string detail) => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not your project", detail: detail);

    private static async Task<IResult> ServeAsync(
        Guid id, bool thumbnail, bool download, HttpContext http, ClaimsPrincipal user, IStorageService storage, CancellationToken ct)
    {
        var file = await storage.GetMetadataAsync(id, ct);
        if (file is null || (thumbnail && !file.HasThumbnail)) return Results.NotFound();

        // A private file: a signed-in caller of its own project with a storage scope. (The platform project's keys may read any project.)
        if (!file.IsPublic)
        {
            if (user.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            if (!ScopeAuthorizationHandler.HasAny(user, [ApiScopes.StorageRead, ApiScopes.StorageUpload, ApiScopes.StorageDelete])) return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden, title: "Missing scope", detail: "Reading a private file needs the storage:read scope.");
            if (!ProjectAccess.TryResolve(user, file.ProjectCode, out _)) return Forbidden("This file belongs to another project.");
        }

        var etag = new EntityTagHeaderValue($"\"{file.FileHashSha256}{(thumbnail ? "-t" : string.Empty)}\"");
        http.Response.Headers[HeaderNames.CacheControl] = file.IsPublic ? "public, max-age=3600" : "private, max-age=300";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (http.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Split(',').Select(t => t.Trim()).Contains(etag.Tag.Value) ))
        {
            http.Response.Headers[HeaderNames.ETag] = etag.ToString();
            return Results.StatusCode(StatusCodes.Status304NotModified);   // not read from the storage, not logged
        }

        var content = await storage.OpenAsync(id, thumbnail, ct);
        if (content is null) return Results.NotFound();

        http.Response.RegisterForDisposeAsync(content);
        var name = thumbnail ? Path.ChangeExtension(file.OriginalFileName, ".webp") : file.OriginalFileName;
        if (!download)
        {
            http.Response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("inline") { FileNameStar = name }.ToString();
            return Results.File(content.Content, content.ContentType, entityTag: etag, enableRangeProcessing: true);
        }

        return Results.File(content.Content, content.ContentType, fileDownloadName: name, entityTag: etag, enableRangeProcessing: true);
    }
}
