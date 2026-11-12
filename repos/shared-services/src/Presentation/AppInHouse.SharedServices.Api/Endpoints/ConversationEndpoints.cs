using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Api.Chat;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>Someone to put in a conversation. The email and chat id are where the notice goes when the user is offline; other members never see them.</summary>
public sealed record MemberRequest(string UserId, string? DisplayName = null, string? Email = null, string? ChatId = null);

/// <summary>
/// <see cref="Type"/> is <c>Direct</c> (exactly one other member) or <c>Group</c> (needs a <see cref="Title"/>). The caller, named by the
/// credential (see the chat user headers), is always a member and owns a group. <see cref="Self"/> is the caller's own display name and contact.
/// </summary>
public sealed record CreateConversationRequest(
    ConversationType Type, string? Title = null, IReadOnlyList<MemberRequest>? Members = null, MemberRequest? Self = null, string? ProjectCode = null);

public sealed record RenameConversationRequest(string Title);

public sealed record SendConversationMessageRequest(string? Content = null, Guid? AttachmentFileId = null);

public sealed record AddConversationMembersRequest(IReadOnlyList<MemberRequest> Members);

public sealed record MarkReadResponse(DateTimeOffset ReadAt);

public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/chat/conversations").WithTags("Conversations").RequireAuthorization(Policies.ChatConverse);

        group.MapPost("/", async (CreateConversationRequest request, HttpContext http, IConversationService service, CancellationToken ct) =>
            {
                if (!Actor(http, request.ProjectCode, out var actor, out var error)) return error!;
                var result = await service.CreateAsync(actor, new CreateConversationCommand(
                    request.Type, request.Title, Specs(request.Members), request.Self is null ? null : Spec(request.Self)), ct);
                return result.Created
                    ? Results.Created($"/api/v1/chat/conversations/{result.Conversation.Id}", result.Conversation)
                    : Results.Ok(result.Conversation);
            })
            .WithName("CreateConversation")
            .WithSummary("Starts a direct conversation (one is never duplicated: the existing one is returned) or a group.");

        group.MapGet("/", async ([FromQuery] string? projectCode, HttpContext http, IConversationService service, CancellationToken ct) =>
                Actor(http, projectCode, out var actor, out var error) ? Results.Ok(await service.ListAsync(actor, ct)) : error!)
            .WithName("ListConversations")
            .WithSummary("The conversations of the calling user, the most recently active first, with the last message and the unread count.");

        group.MapGet("/{id:guid}", async (Guid id, [FromQuery] string? projectCode, HttpContext http, IConversationService service, CancellationToken ct) =>
                Actor(http, projectCode, out var actor, out var error) ? Results.Ok(await service.GetAsync(actor, id, ct)) : error!)
            .WithName("GetConversation")
            .WithSummary("One conversation and its members (members only).");

        group.MapPut("/{id:guid}", async (Guid id, RenameConversationRequest request, [FromQuery] string? projectCode, HttpContext http, IConversationService service, CancellationToken ct) =>
                Actor(http, projectCode, out var actor, out var error) ? Results.Ok(await service.RenameAsync(actor, id, request.Title, ct)) : error!)
            .WithName("RenameConversation")
            .WithSummary("Renames a group (owner or admin).");

        group.MapGet("/{id:guid}/messages", async (
                Guid id, [FromQuery] DateTimeOffset? before, [FromQuery] Guid? beforeId, [FromQuery] int? limit, [FromQuery] string? projectCode,
                HttpContext http, IConversationService service, CancellationToken ct) =>
                Actor(http, projectCode, out var actor, out var error) ? Results.Ok(await service.GetMessagesAsync(actor, id, before, beforeId, limit, ct)) : error!)
            .WithName("GetConversationMessages")
            .WithSummary("The history, newest first, 30 a page by default. Pass nextBefore (and nextBeforeId) of a page as before (and beforeId) for the older one.");

        group.MapPost("/{id:guid}/messages", async (Guid id, SendConversationMessageRequest request, [FromQuery] string? projectCode, HttpContext http, IConversationService service, CancellationToken ct) =>
            {
                if (!Actor(http, projectCode, out var actor, out var error)) return error!;
                var message = await service.SendAsync(actor, id, request.Content ?? string.Empty, request.AttachmentFileId, ct);
                return Results.Created($"/api/v1/chat/conversations/{id}/messages", message);
            })
            .WithName("SendConversationMessage")
            .WithSummary("Sends a message as the calling user (members only). It is pushed to the members who are connected.");

        group.MapPost("/{id:guid}/read", async (Guid id, [FromQuery] string? projectCode, HttpContext http, IConversationService service, CancellationToken ct) =>
            {
                if (!Actor(http, projectCode, out var actor, out var error)) return error!;
                return Results.Ok(new MarkReadResponse(await service.MarkReadAsync(actor, id, ct)));
            })
            .WithName("MarkConversationRead")
            .WithSummary("Marks the conversation as read by the calling user, now.");

        group.MapPost("/{id:guid}/members", async (Guid id, AddConversationMembersRequest request, [FromQuery] string? projectCode, HttpContext http, IConversationService service, CancellationToken ct) =>
                Actor(http, projectCode, out var actor, out var error) ? Results.Ok(await service.AddMembersAsync(actor, id, Specs(request.Members), ct)) : error!)
            .WithName("AddConversationMembers")
            .WithSummary("Adds members to a group (owner or admin).");

        group.MapDelete("/{id:guid}/members/{userId}", async (Guid id, string userId, [FromQuery] string? projectCode, HttpContext http, IConversationService service, CancellationToken ct) =>
            {
                if (!Actor(http, projectCode, out var actor, out var error)) return error!;
                await service.RemoveMemberAsync(actor, id, userId, ct);
                return Results.NoContent();
            })
            .WithName("RemoveConversationMember")
            .WithSummary("Leaves a group (the user is the caller) or removes a member (owner or admin; an admin cannot remove the owner or another admin).");

        // What the portal shows: how busy the chat is and who is online. Needs only config:read, and never shows what was said.
        routes.MapGet("/api/v1/chat/monitor", async ([FromQuery] string? projectCode, System.Security.Claims.ClaimsPrincipal user, IChatMonitor monitor, CancellationToken ct) =>
                ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true)
                    ? Results.Ok(await monitor.GetAsync(project, ct))
                    : ProjectAccess.Forbidden("An API key can only watch the chat of its own project."))
            .RequireAuthorization(Policies.ConfigRead)
            .WithTags("Conversations")
            .WithName("GetChatMonitor")
            .WithSummary("Conversation and message totals, who is online, and the busiest conversations (titles and counts only).");

        return routes;
    }

    private static bool Actor(HttpContext http, string? project, out ChatActor actor, out IResult? error) =>
        ChatActorResolver.TryResolve(http, project, out actor, out error);

    private static MemberSpec Spec(MemberRequest m) => new(m.UserId, m.DisplayName, m.Email, m.ChatId);

    private static IReadOnlyList<MemberSpec> Specs(IReadOnlyList<MemberRequest>? members) => [.. (members ?? []).Where(m => m is not null).Select(Spec)];
}
