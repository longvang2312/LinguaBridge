using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>
/// Real-time chat between users: how busy it is, who is online, and whether the hub can be reached. The bots that send messages out
/// (Telegram, Discord, Slack) are not here any more: they are channels of the notification service (<c>/Notifications/Channels</c>).
/// </summary>
public class ChatModel(ISharedServicesClient client, ILogger<ChatModel> logger) : PageModel
{
    public static readonly string[] SuggestedProjects = ["default", "linguabridge", "identity"];

    [BindProperty(SupportsGet = true)] public string? Project { get; set; }

    /// <summary>Set to ask the hub to negotiate a connection now.</summary>
    [BindProperty(SupportsGet = true)] public bool Check { get; set; }

    public IReadOnlyList<string> Projects { get; private set; } = SuggestedProjects;
    public ChatMonitorDto? Monitor { get; private set; }
    public RealtimeCheckResult? Realtime { get; private set; }
    public string? MonitorError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var project = string.IsNullOrWhiteSpace(Project) ? "default" : Project.Trim().ToLowerInvariant();
        Project = project;
        Projects = [.. SuggestedProjects.Append(project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // It lists titles and counts only, never what was said (the API does not offer that to the portal).
        try
        {
            Monitor = await client.GetChatMonitorAsync(project, cancellationToken);
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not load the chat monitor.");
            MonitorError = UiHelpers.Describe(ex);
        }

        if (Check) Realtime = await client.CheckRealtimeAsync(cancellationToken);
    }
}
