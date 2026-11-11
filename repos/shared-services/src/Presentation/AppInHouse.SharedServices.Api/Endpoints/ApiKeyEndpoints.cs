using System.Security.Claims;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.ApiKeys;
using AppInHouse.SharedServices.Application.Audit;
using AppInHouse.SharedServices.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>Make a key. <see cref="ExpiresInDays"/> left out means the key does not expire (1 to 3650 days otherwise).</summary>
public sealed record CreateApiKeyRequest(string ProjectCode, string Name, string[] Scopes, int? ExpiresInDays = null);

/// <summary>The answer to creating a key. <see cref="ApiKey"/> is the full key: this is the only time it is ever shown.</summary>
public sealed record CreateApiKeyResponse(ApiKeyDto Key, string ApiKey)
{
    public override string ToString() => $"CreateApiKeyResponse {Key.KeyPrefix}...";
}

public sealed record RotateApiKeyResponse(ApiKeyDto Key, string ApiKey, ApiKeyDto OldKey)
{
    public override string ToString() => $"RotateApiKeyResponse {Key.KeyPrefix}...";
}

public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/api-keys").WithTags("API keys");

        // Keys are made, revoked and rotated by the platform only. A project key that could mint keys could give itself every scope
        // and live on after it was revoked; reading the keys of one's own project is harmless and is allowed.
        group.MapGet("", async ([FromQuery] string? projectCode, ClaimsPrincipal user, IApiKeyManager keys, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true))
                    return ProjectAccess.Forbidden("An API key can only list the keys of its own project.");
                return Results.Ok(await keys.ListAsync(project, ct));
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListApiKeys")
            .WithSummary("The keys of a project: name, prefix, scopes, status and dates. Never the key and never its hash.");

        group.MapPost("", async (CreateApiKeyRequest request, ClaimsPrincipal user, HttpContext http, IApiKeyManager keys, CancellationToken ct) =>
            {
                if (!ProjectAccess.IsPlatform(user)) return ProjectAccess.Forbidden("Only the platform can create API keys.");

                var created = await keys.CreateAsync(new CreateApiKeyCommand(request.ProjectCode, request.Name, request.Scopes ?? [], request.ExpiresInDays), ct);
                http.Response.Headers.CacheControl = "no-store";   // the answer holds the only copy of the key
                return Results.Created($"/api/v1/api-keys/{created.Key.Id}", new CreateApiKeyResponse(created.Key, created.PlainTextKey));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("CreateApiKey")
            .WithSummary("Creates a key. The full key is in this answer and nowhere else, ever: only its SHA-256 and a 15-character prefix are stored.");

        group.MapPost("/{id:guid}/revoke", async (Guid id, ClaimsPrincipal user, IApiKeyManager keys, CancellationToken ct) =>
            {
                if (!ProjectAccess.IsPlatform(user)) return ProjectAccess.Forbidden("Only the platform can revoke API keys.");
                return Results.Ok(await keys.RevokeAsync(id, ct));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("RevokeApiKey")
            .WithSummary("Revokes the key at once: the next call that uses it is refused with 401.");

        group.MapPost("/{id:guid}/rotate", async (Guid id, [FromQuery] int? graceDays, ClaimsPrincipal user, HttpContext http, IApiKeyManager keys, CancellationToken ct) =>
            {
                if (!ProjectAccess.IsPlatform(user)) return ProjectAccess.Forbidden("Only the platform can rotate API keys.");

                var rotated = await keys.RotateAsync(id, graceDays ?? 7, ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new RotateApiKeyResponse(rotated.NewKey.Key, rotated.NewKey.PlainTextKey, rotated.OldKey));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("RotateApiKey")
            .WithSummary("Makes a new key with the same project, name and scopes; the old one keeps working for graceDays (default 7), then stops.");

        var audit = routes.MapGroup("/api/v1/audit-logs").WithTags("Audit");

        audit.MapGet("", async (
                [FromQuery] string? actor, [FromQuery] string? action, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
                [FromQuery] int? limit, [FromQuery] int? offset, ClaimsPrincipal user, IAuditLogReader reader, CancellationToken ct) =>
            {
                // The audit trail spans every project (who changed what, anywhere), so it is for the platform only.
                if (!ProjectAccess.IsPlatform(user)) return ProjectAccess.Forbidden("Only the platform can read the audit log.");
                return Results.Ok(await reader.ListAsync(new AuditLogQuery(actor, action, from, to, limit ?? 50, offset ?? 0), ct));
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListAuditLogs")
            .WithSummary("The audit log, newest first (details are cleaned of anything that looks like a secret).");

        audit.MapGet("/actions", async (ClaimsPrincipal user, IAuditLogReader reader, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user) ? Results.Ok(await reader.ActionsAsync(ct)) : ProjectAccess.Forbidden("Only the platform can read the audit log."))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListAuditActions")
            .WithSummary("The actions that appear in the audit log, to fill a filter.");

        return routes;
    }
}
