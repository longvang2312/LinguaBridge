using System.Security.Claims;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Api.Endpoints;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Api.Chat;

/// <summary>
/// Works out who is chatting. There are two kinds of caller:
/// <list type="bullet">
/// <item>An API key belongs to a project's own server. It speaks for one of the project's users, named in the <c>X-Chat-User</c> header
/// (and <c>X-Chat-User-Name</c>); the project is the key's. That server decides who its users are, exactly as it decides who may use its own
/// application, so the key may act as any user of its project, and as nobody in another project.</item>
/// <item>A bearer token (a person signed in at Identity) speaks for itself: its <c>sub</c> is the user id and the headers are ignored, so a
/// token can never be used to impersonate someone else. A token carries no project; like a platform key it names one (<c>X-Chat-Project</c>,
/// or <c>project</c> on the hub address), and is the platform project otherwise.</item>
/// </list>
/// </summary>
public static class ChatActorResolver
{
    public const string UserHeader = "X-Chat-User";
    public const string NameHeader = "X-Chat-User-Name";
    public const string ProjectHeader = "X-Chat-Project";

    public static bool TryResolve(HttpContext http, string? requestedProject, out ChatActor actor, out IResult? error)
    {
        actor = null!;
        error = null;
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            error = Problem(StatusCodes.Status401Unauthorized, "Sign in first", "The call carries no valid API key or token.");
            return false;
        }

        var requested = !string.IsNullOrWhiteSpace(requestedProject) ? requestedProject
            : http.Request.Headers[ProjectHeader].FirstOrDefault() ?? http.Request.Query["project"].FirstOrDefault();
        if (!ProjectAccess.TryResolve(user, requested, out var project) || project is null)
        {
            error = ProjectAccess.Forbidden("An API key can only chat for its own project.");
            return false;
        }

        var isKey = user.FindFirst(ApiKeyDefaults.ProjectClaim) is not null;
        var userId = isKey
            ? http.Request.Headers[UserHeader].FirstOrDefault()?.Trim()
            : (user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.NameIdentifier))?.Value;
        if (!ChatIdentity.IsValidUserId(userId))
        {
            error = Problem(StatusCodes.Status400BadRequest, "Who is chatting?",
                isKey ? $"Send the user's id in the {UserHeader} header (1 to 100 letters, digits or . _ @ : -)."
                      : "The token has no usable subject.");
            return false;
        }

        var name = isKey
            ? http.Request.Headers[NameHeader].FirstOrDefault()
            : (user.FindFirst("name") ?? user.FindFirst("preferred_username") ?? user.FindFirst("email"))?.Value;
        actor = new ChatActor(project, userId!, CleanName(isKey ? Decode(name) : name, userId!),
            ScopeAuthorizationHandler.HasAny(user, [ApiScopes.EmailSend]), ScopeAuthorizationHandler.HasAny(user, [ApiScopes.ChatSend]));
        return true;
    }

    // The name header is percent-encoded UTF-8 (the SDK does that); text that is not valid percent-encoding is used as it is.
    private static string? Decode(string? name) => name is null ? null : Uri.UnescapeDataString(name);

    /// <summary>A display name: no control characters (it ends up in notifications), at most 100 characters, the user id when there is none.</summary>
    public static string CleanName(string? name, string fallback)
    {
        var text = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0) return fallback;
        return text.Length > ChatIdentity.MaxNameLength ? text[..ChatIdentity.MaxNameLength] : text;
    }

    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}
