using System.Security.Claims;
using System.Text.Json;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>
/// Send one message through the project's bot (Telegram, Discord or Slack). <see cref="Recipient"/> is a chat id for Telegram, an optional
/// channel for Slack, unused by Discord; empty means the configuration's default. <see cref="Format"/> (<c>PlainText</c>, <c>Markdown</c>,
/// <c>MarkdownV2</c>) left out means the configuration's default; <c>PlainText</c> is always shown as written. The project defaults to the
/// calling key's project.
/// </summary>
public sealed record SendBotMessageRequest(
    string Text, string? Recipient = null, BotFormat? Format = null, string? ProjectCode = null, Dictionary<string, string>? Metadata = null);

/// <summary>
/// Test a bot configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated. Without it,
/// <see cref="ProviderType"/>, <see cref="Settings"/> and <see cref="Secret"/> (a bot token or a webhook address) are tried from memory and
/// nothing is stored. <see cref="Recipient"/> and <see cref="Message"/> are optional.
/// </summary>
public sealed record TestBotChannelRequest(
    string? Recipient = null, string? Message = null, Guid? ConfigurationId = null,
    ProviderType? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

public sealed record SendBotMessageResponse(bool Success, string Status, Guid? DeliveryLogId, int LatencyMs, string? ConfigurationProject, int? ConfigurationVersion);

public sealed record TestBotChannelResponse(bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

/// <summary>
/// Save a notification channel. <see cref="ChannelType"/> left out means <c>Chat</c> (a bot); <c>Email</c> is the other notification channel.
/// <see cref="Secret"/> is write-only (a bot token or a webhook address, never returned): omit it, or send the mask back, to keep the stored one.
/// </summary>
public sealed record SaveNotificationChannelRequest(
    string ProjectCode, ProviderType ProviderType, string Name, ChannelType? ChannelType = null, JsonElement? Settings = null, string? Secret = null);

public sealed record RollbackNotificationChannelRequest(string ProjectCode, string Name, ChannelType? ChannelType = null, int? TargetVersion = null);

/// <summary>
/// The channels of the notification service: the bots (Telegram, Discord, Slack) and email, with their versions, activation and tests.
/// The older chat addresses (<c>/api/v1/chat/*</c>) are kept as aliases that run the same code, so applications written for them keep working.
/// </summary>
public static class NotificationChannelEndpoints
{
    public static IEndpointRouteBuilder MapNotificationChannelEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/notifications/channels").WithTags("Notification channels");

        group.MapGet("", async (
                [FromQuery] string? projectCode, [FromQuery] string? kind, [FromQuery] ConfigurationStatus? status,
                ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                var wanted = (kind ?? "bot").Trim().ToLowerInvariant() switch
                {
                    "bot" => ProviderCatalog.BotChannels,
                    "email" => [ChannelType.Email],
                    "all" => ProviderCatalog.NotificationChannels,
                    _ => null,
                };
                if (wanted is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Use bot, email or all."] });
                if (!ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true))
                    return ProjectAccess.Forbidden("An API key can only list the channels of its own project.");

                var all = await manager.ListAsync(new ChannelConfigurationQuery(project, null, status), ct);
                return Results.Ok(all.Where(c => wanted.Contains(c.ChannelType)).ToList());
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListNotificationChannels")
            .WithSummary("The bot configurations of the notification service (every version, secrets masked). kind=email or kind=all adds email.");

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                var configuration = await manager.GetAsync(id, ct);
                if (!ProviderCatalog.IsNotificationChannel(configuration.ChannelType)) throw new Application.Common.NotFoundException("There is no such notification channel.");
                return ProjectAccess.CanAct(user, configuration.ProjectCode) ? Results.Ok(configuration) : ProjectAccess.Forbidden("This channel belongs to another project.");
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("GetNotificationChannel");

        group.MapPost("", async (SaveNotificationChannelRequest request, ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                var channel = request.ChannelType ?? ChannelType.Chat;
                if (!ProviderCatalog.IsNotificationChannel(channel))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["ChannelType"] = ["A notification channel is Chat (a bot) or Email."] });
                if (!string.IsNullOrWhiteSpace(request.ProjectCode) && !ProjectAccess.CanAct(user, request.ProjectCode))
                    return ProjectAccess.Forbidden("An API key can only save channels for its own project.");

                var saved = await manager.SaveAsync(new SaveChannelConfigurationCommand(
                    request.ProjectCode, channel, request.ProviderType, request.Name,
                    request.Settings is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } settings ? settings.GetRawText() : "{}",
                    request.Secret), ct);
                return Results.Created($"/api/v1/notifications/channels/{saved.Id}", saved);
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("SaveNotificationChannel")
            .WithSummary("Creates version 1, or the next version, of a bot (or email) channel as a Draft. The secret is stored protected and never returned.");

        group.MapPost("/{id:guid}/activate", async (Guid id, ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                var configuration = await manager.GetAsync(id, ct);
                if (!ProviderCatalog.IsNotificationChannel(configuration.ChannelType)) throw new Application.Common.NotFoundException("There is no such notification channel.");
                return ProjectAccess.CanAct(user, configuration.ProjectCode)
                    ? Results.Ok(await manager.ActivateAsync(id, ct))
                    : ProjectAccess.Forbidden("This channel belongs to another project.");
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("ActivateNotificationChannel")
            .WithSummary("Makes this version the active one; the previous active version is archived.");

        group.MapPost("/rollback", async (RollbackNotificationChannelRequest request, ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                var channel = request.ChannelType ?? ChannelType.Chat;
                if (!ProviderCatalog.IsNotificationChannel(channel))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["ChannelType"] = ["A notification channel is Chat (a bot) or Email."] });
                if (!string.IsNullOrWhiteSpace(request.ProjectCode) && !ProjectAccess.CanAct(user, request.ProjectCode))
                    return ProjectAccess.Forbidden("An API key can only roll back channels of its own project.");

                return Results.Ok(await manager.RollbackAsync(new RollbackConfigurationCommand(request.ProjectCode, channel, request.Name, request.TargetVersion), ct));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("RollbackNotificationChannel");

        group.MapPost("/test", TestBotAsync)
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("TestNotificationChannel")
            .WithSummary("Sends a test message through a bot. Without a configuration id the settings in the request are tried from memory and nothing is saved or activated.");

        group.MapPost("/send", SendNotificationChannelMessageAsync)
            .RequireAuthorization(Policies.ChatSend)
            .WithName("SendBotMessage")
            .WithSummary("Sends a message through the project's active bot (or the default project's) and logs the delivery. For a message built from a template use POST /api/v1/notifications/dispatch.");

        group.MapGet("/delivery-logs", ListBotDeliveriesAsync)
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListBotDeliveryLogs")
            .WithSummary("The most recent bot deliveries (no message text, no token, no webhook address).");

        return routes;
    }

    private static async Task<IResult> SendNotificationChannelMessageAsync(SendBotMessageRequest request, ClaimsPrincipal user, INotificationBotDispatcher dispatcher, CancellationToken ct)
    {
        if (!ProjectAccess.TryResolve(user, request.ProjectCode, out var project)) return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Not your project", detail: "An API key can only send for its own project.");

        var result = await dispatcher.SendAsync(new SendBotMessageCommand(
            project ?? ProjectAccess.PlatformProject, request.Text, request.Recipient, request.Format, request.Metadata), ct);

        return result.Failure switch
        {
            BotFailure.None => Results.Ok(new SendBotMessageResponse(true, "Sent", result.DeliveryLogId, result.LatencyMs, result.ConfigurationProject, result.ConfigurationVersion)),
            BotFailure.NoConfiguration => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No chat configuration", detail: result.Error),
            _ => Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The message could not be delivered", detail: result.Error),
        };
    }

    private static async Task<IResult> TestBotAsync(TestBotChannelRequest request, ClaimsPrincipal user, IChannelConfigManager manager, INotificationBotTestService tests, CancellationToken ct)
    {
        if (await ProjectAccess.CheckConfigurationAsync(user, request.ConfigurationId, manager, ct) is { } denied) return denied;

        var result = await tests.TestAsync(new TestBotCommand(
            request.Recipient, request.Message, request.ConfigurationId, request.ProviderType,
            request.Settings is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } settings ? settings.GetRawText() : null,
            request.Secret), ct);
        return Results.Ok(new TestBotChannelResponse(result.Success, result.Message, result.LatencyMs, result.ProviderType.ToString(), result.UsedSavedConfiguration, result.ConfigurationId));
    }

    private static async Task<IResult> ListBotDeliveriesAsync([FromQuery] string? projectCode, [FromQuery] int? limit, ClaimsPrincipal user, INotificationBotDispatcher dispatcher, CancellationToken ct)
    {
        if (!ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true)) return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden, title: "Not your project", detail: "An API key can only read the deliveries of its own project.");

        return Results.Ok(await dispatcher.RecentAsync(project, limit ?? 20, ct));
    }
}
