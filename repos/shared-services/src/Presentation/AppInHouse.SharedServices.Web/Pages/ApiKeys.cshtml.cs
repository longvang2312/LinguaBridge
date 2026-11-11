using System.ComponentModel.DataAnnotations;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>What the "new key" form posts.</summary>
public sealed class NewKeyForm
{
    [Required, StringLength(50)] public string ProjectCode { get; set; } = "default";
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;

    /// <summary>The scopes ticked on the form.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>Empty for a key that does not expire.</summary>
    public int? ExpiresInDays { get; set; }
}

/// <summary>
/// The API keys of the projects. A key is shown in full once, in the answer to the request that makes it (the page is returned
/// directly, not redirected to, so the key is never stored in a cookie or a session). The page is not cached.
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ApiKeysModel(ISharedServicesClient client, ILogger<ApiKeysModel> logger) : PageModel
{
    public static readonly string[] Scopes = ["email:send", "chat:send", "storage:upload", "storage:read", "storage:delete", "config:read", "config:write"];
    public static readonly (string Label, int? Days)[] Lifetimes = [("30 ngày", 30), ("90 ngày", 90), ("1 năm", 365), ("Không hết hạn", null)];
    public static readonly string[] SuggestedProjects = ["default", "linguabridge", "identity"];

    /// <summary>The project the list is filtered to; empty shows every project.</summary>
    [BindProperty(SupportsGet = true)] public string? Project { get; set; }

    [BindProperty] public NewKeyForm Form { get; set; } = new();

    public IReadOnlyList<ApiKeyDto> Keys { get; private set; } = [];
    public IReadOnlyList<string> Projects { get; private set; } = SuggestedProjects;

    /// <summary>Set only on the response to creating or rotating a key: the one time the full key is shown.</summary>
    public string? PlainTextKey { get; private set; }

    public ApiKeyDto? CreatedKey { get; private set; }

    /// <summary>"created" or "rotated": which message the modal gives.</summary>
    public string? CreatedHow { get; private set; }

    public ApiKeyDto? RetiredKey { get; private set; }

    public string? LoadError { get; private set; }
    public string? FormError { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var created = await client.CreateApiKeyAsync(new CreateApiKeyRequest(
                Form.ProjectCode.Trim().ToLowerInvariant(), Form.Name.Trim(), [.. Form.Scopes], Form.ExpiresInDays), cancellationToken);
            PlainTextKey = created.ApiKey;
            CreatedKey = created.Key;
            CreatedHow = "created";
            Form = new NewKeyForm { ProjectCode = created.Key.ProjectCode };
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not create an API key for {Project}.", Form.ProjectCode);
            FormError = Describe(ex);
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRotateAsync(Guid id, int graceDays, CancellationToken cancellationToken)
    {
        try
        {
            var rotated = await client.RotateApiKeyAsync(id, graceDays, cancellationToken);
            PlainTextKey = rotated.ApiKey;
            CreatedKey = rotated.Key;
            RetiredKey = rotated.OldKey;
            CreatedHow = "rotated";
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not rotate API key {Id}.", id);
            ErrorMessage = Describe(ex);
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, string? project, CancellationToken cancellationToken)
    {
        try
        {
            var revoked = await client.RevokeApiKeyAsync(id, cancellationToken);
            Message = $"Đã thu hồi key {revoked.Name} ({revoked.KeyPrefix}…). Key này không dùng được nữa.";
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not revoke API key {Id}.", id);
            ErrorMessage = Describe(ex);
        }

        return RedirectToPage(new { project });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var project = string.IsNullOrWhiteSpace(Project) ? null : Project.Trim().ToLowerInvariant();
        Project = project;
        try
        {
            var all = await client.ListApiKeysAsync(null, cancellationToken);
            Projects = [.. SuggestedProjects.Concat(all.Select(k => k.ProjectCode)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            Keys = [.. all.Where(k => project is null || k.ProjectCode == project)];
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not load the API keys.");
            LoadError = UiHelpers.Describe(ex);
        }
    }

    public static string StatusBadge(string status) => status switch
    {
        "Active" => "text-bg-success",
        "Revoked" => "text-bg-danger",
        _ => "text-bg-secondary",
    };

    public static string StatusLabel(string status) => status switch
    {
        "Active" => "Đang hoạt động",
        "Revoked" => "Đã thu hồi",
        "Expired" => "Hết hạn",
        _ => status,
    };

    private static bool IsApiProblem(Exception ex) => ex is SharedServicesApiException or HttpRequestException or TaskCanceledException;

    private static string Describe(Exception ex) => ex is SharedServicesApiException { Errors.Count: > 0 } api
        ? string.Join(" ", api.Errors.SelectMany(e => e.Value))
        : UiHelpers.Describe(ex);
}
