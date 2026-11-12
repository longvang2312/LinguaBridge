using System.ComponentModel.DataAnnotations;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages.Projects;

/// <summary>What the "new project" and "edit project" forms post. An empty quota means no limit; the storage quota is in gigabytes.</summary>
public sealed class ProjectForm
{
    [Required, StringLength(50)] public string ProjectCode { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string? Description { get; set; }
    public string Environment { get; set; } = "Staging";
    [Range(0, int.MaxValue)] public int? DailyEmailQuota { get; set; }
    [Range(0, int.MaxValue)] public int? DailyChatQuota { get; set; }
    [Range(0, 1_000_000)] public decimal? StorageQuotaGb { get; set; }
    public bool IsActive { get; set; } = true;
}

public class IndexModel(ISharedServicesClient client, ILogger<IndexModel> logger) : PageModel
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    public sealed record ProjectSummary(ProjectTenantDto Project, ProjectUsageDto? Usage, int ApiKeys);

    [BindProperty] public ProjectForm Form { get; set; } = new();

    public IReadOnlyList<ProjectSummary> Projects { get; private set; } = [];
    public int TotalApiKeys { get; private set; }
    public string? LoadError { get; private set; }
    public string? FormError { get; private set; }

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return await Invalid(cancellationToken);

        try
        {
            var created = await client.CreateProjectAsync(new CreateProjectRequest(
                Form.ProjectCode.Trim().ToLowerInvariant(), Form.Name.Trim(), Form.Description, Form.Environment,
                Form.DailyEmailQuota, Form.DailyChatQuota, ToBytes(Form.StorageQuotaGb)), cancellationToken);
            Message = $"Đã tạo dự án {created.ProjectCode}.";
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            return await Failed(ex, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return await Invalid(cancellationToken);

        try
        {
            var updated = await client.UpdateProjectAsync(Form.ProjectCode.Trim().ToLowerInvariant(), new UpdateProjectRequest(
                Form.Name.Trim(), Form.Description, Form.Environment, Form.DailyEmailQuota, Form.DailyChatQuota, ToBytes(Form.StorageQuotaGb), Form.IsActive), cancellationToken);
            Message = $"Đã cập nhật dự án {updated.ProjectCode}.";
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            return await Failed(ex, cancellationToken);
        }
    }

    private async Task<IActionResult> Invalid(CancellationToken cancellationToken)
    {
        FormError = "Thông tin chưa hợp lệ: " + string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).Where(m => !string.IsNullOrEmpty(m)).DefaultIfEmpty("kiểm tra lại các ô đã nhập."));
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<IActionResult> Failed(Exception ex, CancellationToken cancellationToken)
    {
        logger.LogWarning(ex, "Could not save project {Project}.", Form.ProjectCode);
        FormError = ex is SharedServicesApiException { Errors.Count: > 0 } api ? string.Join(" ", api.Errors.SelectMany(e => e.Value)) : UiHelpers.Describe(ex);
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var projects = await client.GetProjectsAsync(cancellationToken);
            IReadOnlyList<ApiKeyDto> keys = [];
            try
            {
                keys = await client.ListApiKeysAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not load the API keys for the project overview.");
            }

            TotalApiKeys = keys.Count(k => k.Status == "Active");
            var summaries = new List<ProjectSummary>();
            foreach (var project in projects)
            {
                ProjectUsageDto? usage = null;
                try
                {
                    usage = await client.GetProjectUsageAsync(project.ProjectCode, cancellationToken);
                }
                catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
                {
                    logger.LogWarning(ex, "Could not load the usage of project {Project}.", project.ProjectCode);
                }

                summaries.Add(new ProjectSummary(project, usage, keys.Count(k => k.Status == "Active" && string.Equals(k.ProjectCode, project.ProjectCode, StringComparison.OrdinalIgnoreCase))));
            }

            Projects = summaries;
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not load the projects.");
            LoadError = UiHelpers.Describe(ex);
        }
    }

    private static long? ToBytes(decimal? gigabytes) => gigabytes is { } gb ? (long)(gb * Gigabyte) : null;

    public static string Quota(int? limit, string unit) => limit is { } n ? $"{n:n0} {unit}" : "Không giới hạn";

    public static string QuotaBytes(long? limit) => limit is { } n ? UiHelpers.FormatBytes(n) : "Không giới hạn";

    /// <summary>How much of a quota is used, 0 to 100; null when there is no limit.</summary>
    public static int? Percent(long used, long? limit) => limit is > 0 ? (int)Math.Min(100, used * 100 / limit.Value) : limit == 0 ? 100 : null;
}
