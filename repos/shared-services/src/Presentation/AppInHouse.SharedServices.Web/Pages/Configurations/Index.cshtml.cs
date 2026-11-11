using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages.Configurations;

public class IndexModel(ISharedServicesClient client, ILogger<IndexModel> logger) : PageModel
{
    public static readonly string[] Channels = ["Email", "Chat", "Telegram", "Storage"];

    [BindProperty(SupportsGet = true)] public string? Project { get; set; }
    [BindProperty(SupportsGet = true)] public string? Channel { get; set; }

    public IReadOnlyList<ChannelConfigurationResponse> Items { get; private set; } = [];

    /// <summary>The projects that have a configuration, for the filter.</summary>
    public IReadOnlyList<string> Projects { get; private set; } = [];

    public string? LoadError { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The project list comes from the unfiltered list, so the filter keeps offering every project.
            var all = await client.ListConfigurationsAsync(cancellationToken: cancellationToken);
            Projects = [.. all.Select(c => c.ProjectCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            Items = [.. all.Where(c =>
                (string.IsNullOrWhiteSpace(Project) || c.ProjectCode == Project)
                && (string.IsNullOrWhiteSpace(Channel) || c.ChannelType.Equals(Channel, StringComparison.OrdinalIgnoreCase)))];
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not load the configurations.");
            LoadError = UiHelpers.Describe(ex);
        }
    }

    /// <summary>Makes a version active. The anti-forgery token of the form is validated before this runs.</summary>
    public async Task<IActionResult> OnPostActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var activated = await client.ActivateConfigurationAsync(id, cancellationToken);
            Message = $"Đã kích hoạt {activated.Name} phiên bản {activated.Version}.";
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not activate {Id}.", id);
            ErrorMessage = UiHelpers.Describe(ex);
        }

        return RedirectToPage(new { Project, Channel });
    }

    /// <summary>Goes back to an earlier version of the series.</summary>
    public async Task<IActionResult> OnPostRollbackAsync(string projectCode, string channelType, string name, int version, CancellationToken cancellationToken)
    {
        try
        {
            var restored = await client.RollbackConfigurationAsync(new RollbackConfigurationRequest(projectCode, channelType, name, version), cancellationToken);
            Message = $"Đã quay về {restored.Name} phiên bản {restored.Version}.";
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not roll back {Name}.", name);
            ErrorMessage = UiHelpers.Describe(ex);
        }

        return RedirectToPage(new { Project, Channel });
    }
}
