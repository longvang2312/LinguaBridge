using System.Security.Claims;
using System.Text.Json;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Notifications;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>
/// Send a notification built from a template. <see cref="Data"/> is an object whose values (text, numbers, true/false) fill the placeholders.
/// With <see cref="Queue"/> the messages are queued (answer 202) and retried by the service instead of being sent now.
/// </summary>
public sealed record DispatchNotificationRequest(
    string TemplateCode, JsonElement? Data = null, string? RecipientEmail = null, string? RecipientChatId = null,
    string? ProjectCode = null, NotificationPriority? Priority = null, bool? Queue = null);

public sealed record DispatchNotificationResponse(
    bool Success, string Status, IReadOnlyList<NotificationChannelResult> Channels, int LatencyMs, Guid LogId, IReadOnlyList<Guid> JobIds);

public sealed record CreateWebhookSubscriptionRequest(string Name, string TargetUrl, IReadOnlyList<string> Events, string? SecretKey = null, string? ProjectCode = null);

public sealed record CreateWebhookSubscriptionResponse(WebhookSubscriptionDto Subscription, string SecretKey);

public sealed record CreateProjectRequest(
    string ProjectCode, string Name, string? Description = null, ProjectEnvironment? Environment = null,
    int? DailyEmailQuota = null, int? DailyChatQuota = null, long? StorageQuotaBytes = null);

public sealed record UpdateProjectRequest(
    string Name, string? Description = null, ProjectEnvironment? Environment = null,
    int? DailyEmailQuota = null, int? DailyChatQuota = null, long? StorageQuotaBytes = null, bool? IsActive = null);

public sealed record QueueThumbnailResponse(Guid JobId);

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder routes)
    {
        MapNotifications(routes);
        MapJobs(routes);
        MapWebhooks(routes);
        MapProjects(routes);
        MapMetrics(routes);
        return routes;
    }

    private static IResult PlatformOnly(string what) => ProjectAccess.Forbidden($"Only the platform can {what}.");

    // ------------------------------------------------------------------------------------------------ notifications

    private static void MapNotifications(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/notifications").WithTags("Notifications");

        group.MapPost("/dispatch", async (DispatchNotificationRequest request, ClaimsPrincipal user, INotificationDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, request.ProjectCode, out var project)) return ProjectAccess.Forbidden("An API key can only send for its own project.");

                var data = ReadData(request.Data, out var dataError);
                if (dataError is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["Data"] = [dataError] });

                var result = await dispatcher.DispatchAsync(new DispatchNotificationCommand(
                    project ?? ProjectAccess.PlatformProject, request.TemplateCode, data, request.RecipientEmail, request.RecipientChatId, request.Priority, request.Queue ?? false,
                    CanSendEmail: ScopeAuthorizationHandler.HasAny(user, [ApiScopes.EmailSend]),
                    CanSendChat: ScopeAuthorizationHandler.HasAny(user, [ApiScopes.ChatSend, ApiScopes.TelegramSend])), ct);

                var response = new DispatchNotificationResponse(result.Success, result.Status, result.Channels, result.LatencyMs, result.LogId, result.JobIds);
                if (result.Status == "Queued") return Results.Accepted($"/api/v1/notifications/logs", response);
                if (result.Success) return Results.Ok(response);
                return Results.Problem(
                    statusCode: StatusCodes.Status502BadGateway, title: "The notification could not be delivered",
                    detail: string.Join(" ", result.Channels.Select(c => $"{c.Channel}: {c.Error}")),
                    extensions: new Dictionary<string, object?> { ["logId"] = result.LogId, ["channels"] = result.Channels });
            })
            .RequireAuthorization(Policies.NotificationSend)
            .WithName("DispatchNotification")
            .WithSummary("Fills a template and sends it by email, chat or both (High and Critical ones fall back to the other channel when the first fails).");

        group.MapGet("/templates", async (INotificationDispatcher dispatcher, CancellationToken ct) => Results.Ok(await dispatcher.ListTemplatesAsync(ct)))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListNotificationTemplates")
            .WithSummary("The notification templates, with the placeholders each one needs.");

        group.MapGet("/logs", async ([FromQuery] string? projectCode, [FromQuery] int? limit, ClaimsPrincipal user, INotificationDispatcher dispatcher, CancellationToken ct) =>
                ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true)
                    ? Results.Ok(await dispatcher.ListLogsAsync(project, limit ?? 20, ct))
                    : ProjectAccess.Forbidden("An API key can only read the notifications of its own project."))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListNotificationLogs")
            .WithSummary("The most recent notifications.");
    }

    /// <summary>Turns the <c>data</c> object into text values; anything that is not text, a number or true/false is refused.</summary>
    private static Dictionary<string, string>? ReadData(JsonElement? data, out string? error)
    {
        error = null;
        if (data is not { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element) return null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Data must be an object.";
            return null;
        }

        var values = new Dictionary<string, string>();
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String: values[property.Name] = property.Value.GetString() ?? string.Empty; break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False: values[property.Name] = property.Value.GetRawText(); break;
                case JsonValueKind.Null: break;
                default:
                    error = $"The value of '{property.Name}' must be text, a number or true/false.";
                    return null;
            }
        }

        return values;
    }

    // ------------------------------------------------------------------------------------------------ jobs

    private static void MapJobs(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/jobs").WithTags("Jobs");

        group.MapGet("/queues", async (ClaimsPrincipal user, IJobQueue jobs, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user) ? Results.Ok(await jobs.GetQueuesAsync(ct)) : PlatformOnly("see the queues"))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("GetJobQueues")
            .WithSummary("The five queues: waiting, running, completed in the last 24 hours and failed jobs.");

        group.MapPost("/queues/{queueName}/retry-failed", async (string queueName, ClaimsPrincipal user, IJobQueue jobs, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user) ? Results.Ok(await jobs.RetryFailedAsync(queueName, ct)) : PlatformOnly("retry failed jobs"))
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("RetryFailedJobs")
            .WithSummary("Puts the failed jobs of a queue back in it, each with a fresh set of attempts.");

        group.MapGet("/schedules", async (ClaimsPrincipal user, IScheduleService schedules, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user) ? Results.Ok(await schedules.ListAsync(ct)) : PlatformOnly("see the schedules"))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("GetJobSchedules")
            .WithSummary("The scheduled tasks (cron, UTC) with their last and next run.");

        group.MapPost("/schedules/{id}/run", async (string id, ClaimsPrincipal user, IScheduleService schedules, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user) ? Results.Ok(await schedules.RunNowAsync(id, ct)) : PlatformOnly("run a scheduled task"))
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("RunScheduledTask")
            .WithSummary("Runs a scheduled task now, without changing its schedule.");

        routes.MapPost("/api/v1/storage/files/{id:guid}/thumbnail", async (Guid id, ClaimsPrincipal user, IStorageService storage, IJobQueue jobs, CancellationToken ct) =>
            {
                var file = await storage.GetMetadataAsync(id, ct);
                if (file is null) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "There is no such file.");
                if (!ProjectAccess.CanAct(user, file.ProjectCode)) return ProjectAccess.Forbidden("This file belongs to another project.");
                if (!file.HasThumbnail) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not an image", detail: "Only an image has a thumbnail.");

                var jobId = await jobs.EnqueueAsync(JobQueues.MediaThumbnail, "rebuild", new ThumbnailJobPayload(id), ct);
                return Results.Accepted($"/api/v1/storage/files/{id}", new QueueThumbnailResponse(jobId));
            })
            .RequireAuthorization(Policies.StorageUpload)
            .WithTags("Storage")
            .WithName("QueueThumbnailRebuild")
            .WithSummary("Queues the rebuilding of the thumbnail of an image (answer 202); the media-thumbnail queue does it and retries if the storage is unavailable.");
    }

    // ------------------------------------------------------------------------------------------------ webhooks

    private static void MapWebhooks(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/webhooks").WithTags("Webhooks");

        // Called by the outside world (Telegram, GitHub, Stripe), which has no API key: the signature of each source is the proof.
        group.MapPost("/incoming/{source}", async (string source, HttpRequest request, IInboundWebhookReceiver receiver, CancellationToken ct) =>
            {
                if (request.ContentLength is { } declared && declared > InboundSources.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                // Read at most one byte more than allowed, so an oversized body is noticed without being held in memory.
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > InboundSources.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                var result = await receiver.ReceiveAsync(source, headers, buffer.ToArray(), ct);
                return result.Outcome switch
                {
                    InboundOutcome.Accepted => Results.Accepted(value: new { accepted = true, id = result.LogId }),
                    InboundOutcome.UnknownSource => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown source", detail: "There is no webhook source with that name."),
                    InboundOutcome.NotConfigured => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Source not configured", detail: "This installation has no secret for this source yet."),
                    InboundOutcome.TooLarge => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
                    _ => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid signature", detail: "The signature or secret token is missing or wrong."),
                };
            })
            .AllowAnonymous()
            .WithName("ReceiveInboundWebhook")
            .WithSummary("Receives a webhook of a third party (telegram, github, stripe), verifies its signature, logs it and queues its processing.");

        group.MapGet("/inbound", async (ClaimsPrincipal user, IInboundWebhookReceiver receiver, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user) ? Results.Ok(await receiver.GetSourcesAsync(ct)) : PlatformOnly("see the inbound sources"))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListInboundWebhookSources")
            .WithSummary("The inbound sources: address, how the call is verified, whether this installation has the secret, and the last 24 hours.");

        group.MapGet("/subscriptions", async ([FromQuery] string? projectCode, ClaimsPrincipal user, IWebhookManager webhooks, CancellationToken ct) =>
                ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true)
                    ? Results.Ok(await webhooks.ListAsync(project, ct))
                    : ProjectAccess.Forbidden("An API key can only see the subscriptions of its own project."))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListWebhookSubscriptions")
            .WithSummary("The outbound subscriptions (never the signing key, only its last four characters).");

        group.MapPost("/subscriptions", async (CreateWebhookSubscriptionRequest request, ClaimsPrincipal user, HttpContext http, IWebhookManager webhooks, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, request.ProjectCode, out var project)) return ProjectAccess.Forbidden("An API key can only subscribe for its own project.");

                var created = await webhooks.CreateAsync(new CreateWebhookSubscriptionCommand(
                    project ?? ProjectAccess.PlatformProject, request.Name, request.TargetUrl, request.Events ?? [], request.SecretKey), ct);
                http.Response.Headers.CacheControl = "no-store";   // the answer holds the only copy of a generated signing key
                return Results.Created($"/api/v1/webhooks/subscriptions/{created.Subscription.Id}", new CreateWebhookSubscriptionResponse(created.Subscription, created.SecretKey));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("CreateWebhookSubscription")
            .WithSummary("Subscribes an address to events (email.sent, email.failed, chat.sent, chat.failed, file.uploaded, webhook.received or *). Every post is signed: X-AIH-Signature: sha256={hmac of the body}.");

        group.MapDelete("/subscriptions/{id:guid}", async (Guid id, ClaimsPrincipal user, IWebhookManager webhooks, CancellationToken ct) =>
            {
                var subscription = await webhooks.GetAsync(id, ct);
                if (!ProjectAccess.CanAct(user, subscription.ProjectCode)) return ProjectAccess.Forbidden("This subscription belongs to another project.");
                await webhooks.DeleteAsync(id, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("DeleteWebhookSubscription")
            .WithSummary("Deletes a subscription; deliveries still waiting for it are dropped.");

        group.MapPost("/subscriptions/{id:guid}/test", async (Guid id, ClaimsPrincipal user, IWebhookManager webhooks, CancellationToken ct) =>
            {
                var subscription = await webhooks.GetAsync(id, ct);
                if (!ProjectAccess.CanAct(user, subscription.ProjectCode)) return ProjectAccess.Forbidden("This subscription belongs to another project.");
                return Results.Ok(await webhooks.TestAsync(id, ct));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("TestWebhookSubscription")
            .WithSummary("Posts a webhook.test event to the subscription now and reports what the application answered.");
    }

    // ------------------------------------------------------------------------------------------------ projects

    private static void MapProjects(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/projects").WithTags("Projects");

        group.MapGet("", async (ClaimsPrincipal user, IProjectManager projects, CancellationToken ct) =>
            {
                // A key of one project sees only its own project; the platform's keys see all of them.
                var own = user.FindFirst(ApiKeyDefaults.ProjectClaim)?.Value;
                return Results.Ok(await projects.ListAsync(ProjectAccess.IsPlatform(user) ? null : own, ct));
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListProjects")
            .WithSummary("The projects with their quotas.");

        group.MapPost("", async (CreateProjectRequest request, ClaimsPrincipal user, IProjectManager projects, CancellationToken ct) =>
            {
                if (!ProjectAccess.IsPlatform(user)) return PlatformOnly("create projects");
                var created = await projects.CreateAsync(new CreateProjectCommand(
                    request.ProjectCode, request.Name, request.Description, request.Environment ?? ProjectEnvironment.Staging,
                    request.DailyEmailQuota, request.DailyChatQuota, request.StorageQuotaBytes), ct);
                return Results.Created($"/api/v1/projects/{created.ProjectCode}/usage", created);
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("CreateProject")
            .WithSummary("Registers a project. A quota left out means no limit.");

        group.MapPut("/{code}", async (string code, UpdateProjectRequest request, ClaimsPrincipal user, IProjectManager projects, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user)
                    ? Results.Ok(await projects.UpdateAsync(code, new UpdateProjectCommand(
                        request.Name, request.Description, request.Environment ?? ProjectEnvironment.Staging,
                        request.DailyEmailQuota, request.DailyChatQuota, request.StorageQuotaBytes, request.IsActive ?? true), ct))
                    : PlatformOnly("change projects"))
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("UpdateProject")
            .WithSummary("Changes a project's description, environment, quotas and whether it is active. The whole project is replaced: send every field.");

        group.MapGet("/{code}/usage", async (string code, ClaimsPrincipal user, IProjectManager projects, CancellationToken ct) =>
                ProjectAccess.CanAct(user, code.ToLowerInvariant())
                    ? Results.Ok(await projects.GetUsageAsync(code, ct))
                    : ProjectAccess.Forbidden("An API key can only see the usage of its own project."))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("GetProjectUsage")
            .WithSummary("What the project has used of its daily and storage quotas.");
    }

    // ------------------------------------------------------------------------------------------------ metrics

    private static void MapMetrics(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/metrics").WithTags("Monitoring");

        group.MapGet("/overview", async (ClaimsPrincipal user, IMetricsCollector metrics, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user) ? Results.Ok(await metrics.GetOverviewAsync(cancellationToken: ct)) : PlatformOnly("read the metrics"))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("GetMetricsOverview")
            .WithSummary("The figures of the last 24 hours across the eight services, and the health of each.");

        group.MapGet("/health-status", async (ClaimsPrincipal user, IMetricsCollector metrics, CancellationToken ct) =>
                ProjectAccess.IsPlatform(user) ? Results.Ok(await metrics.GetHealthAsync(ct)) : PlatformOnly("read the health of the services"))
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("GetHealthStatus")
            .WithSummary("Healthy, Degraded or Idle for each of the eight services.");
    }
}
