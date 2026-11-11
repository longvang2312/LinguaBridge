using System.Security.Claims;
using System.Text.Json;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.Chat;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>
/// Send one chat message. <see cref="Recipient"/> is a chat id for Telegram, an optional channel for Slack, unused by Discord; empty means
/// the configuration's default. <see cref="Format"/> (<c>PlainText</c>, <c>Markdown</c>, <c>MarkdownV2</c>) left out means the configuration's
/// default; <c>PlainText</c> is always shown as written. The project defaults to the calling key's project.
/// </summary>
public sealed record SendChatMessageRequest(
    string Text, string? Recipient = null, ChatFormat? Format = null, string? ProjectCode = null, Dictionary<string, string>? Metadata = null);

/// <summary>
/// Test a chat configuration. With <see cref="ConfigurationId"/> the saved version is tested and its last test result is updated. Without it,
/// <see cref="ProviderType"/>, <see cref="Settings"/> and <see cref="Secret"/> (a bot token or a webhook address) are tried from memory and
/// nothing is stored. <see cref="Recipient"/> and <see cref="Message"/> are optional.
/// </summary>
public sealed record SendTestChatMessageRequest(
    string? Recipient = null, string? Message = null, Guid? ConfigurationId = null,
    ProviderType? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

public sealed record SendChatMessageResponse(bool Success, string Status, Guid? DeliveryLogId, int LatencyMs, string? ConfigurationProject, int? ConfigurationVersion);

public sealed record SendTestChatMessageResponse(bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/chat").WithTags("Chat");

        group.MapPost("/send", async (SendChatMessageRequest request, ClaimsPrincipal user, IChatDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, request.ProjectCode, out var project)) return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden, title: "Not your project", detail: "An API key can only send for its own project.");

                var result = await dispatcher.SendAsync(new SendChatCommand(
                    project ?? ProjectAccess.PlatformProject, request.Text, request.Recipient, request.Format, request.Metadata), ct);

                return result.Failure switch
                {
                    ChatFailure.None => Results.Ok(new SendChatMessageResponse(true, "Sent", result.DeliveryLogId, result.LatencyMs, result.ConfigurationProject, result.ConfigurationVersion)),
                    ChatFailure.NoConfiguration => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No chat configuration", detail: result.Error),
                    _ => Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The message could not be delivered", detail: result.Error),
                };
            })
            .RequireAuthorization(Policies.ChatSend)
            .WithName("SendChatMessage")
            .WithSummary("Sends a chat message with the project's active chat configuration (or the default project's) and logs the delivery.");

        // POST /api/v1/telegram/test is the older address of the same endpoint, kept so callers written for it keep working.
        var test = async (SendTestChatMessageRequest request, ClaimsPrincipal user, IChannelConfigManager manager, IChatTestService tests, CancellationToken ct) =>
        {
            if (await ProjectAccess.CheckConfigurationAsync(user, request.ConfigurationId, manager, ct) is { } denied) return denied;

            var result = await tests.TestAsync(new TestChatCommand(
                request.Recipient, request.Message, request.ConfigurationId, request.ProviderType,
                request.Settings is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } settings ? settings.GetRawText() : null,
                request.Secret), ct);
            return Results.Ok(new SendTestChatMessageResponse(result.Success, result.Message, result.LatencyMs, result.ProviderType.ToString(), result.UsedSavedConfiguration, result.ConfigurationId));
        };
        group.MapPost("/test", test)
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("SendTestChatMessage")
            .WithSummary("Sends a test message. Without a configuration id the settings in the request are tried from memory and nothing is saved or activated.");
        routes.MapPost("/api/v1/telegram/test", test)
            .RequireAuthorization(Policies.ConfigWrite)
            .WithTags("Chat")
            .WithName("SendTestTelegramMessage")
            .WithSummary("The older address of POST /api/v1/chat/test.");

        group.MapGet("/delivery-logs", async ([FromQuery] string? projectCode, [FromQuery] int? limit, ClaimsPrincipal user, IChatDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true)) return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden, title: "Not your project", detail: "An API key can only read the deliveries of its own project.");

                return Results.Ok(await dispatcher.RecentAsync(project, limit ?? 20, ct));
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListChatDeliveryLogs")
            .WithSummary("The most recent chat deliveries (no message text, no token, no webhook address).");

        return routes;
    }
}
