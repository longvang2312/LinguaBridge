using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

public class IndexModel(ISharedServicesClient client, ILogger<IndexModel> logger) : PageModel
{
    public sealed record ChannelSummary(string Channel, string Title, string Icon, string Page, int Active, int Draft);

    public IReadOnlyList<ChannelSummary> Channels { get; private set; } = [];
    public string? LoadError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelConfigurationResponse> all = [];
        try
        {
            all = await client.ListConfigurationsAsync(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not load the configurations for the overview.");
            LoadError = UiHelpers.Describe(ex);
        }

        int Count(string channel, string status) => all.Count(c => c.ChannelType == channel && c.Status == status);

        Channels =
        [
            new("Email", "Email", "bi-envelope", "/Email", Count("Email", "Active"), Count("Email", "Draft")),
            new("Telegram", "Telegram Bot", "bi-chat-dots", "/Telegram", Count("Telegram", "Active"), Count("Telegram", "Draft")),
            new("Storage", "Lưu trữ & Media", "bi-images", "/Storage", Count("Storage", "Active"), Count("Storage", "Draft")),
        ];
    }
}
