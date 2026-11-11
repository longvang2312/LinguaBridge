using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>What the storage form posts. Fields of a provider other than the chosen one are ignored.</summary>
public sealed class StorageForm
{
    [Required, StringLength(50)] public string ProjectCode { get; set; } = "default";
    [Required, StringLength(100)] public string Name { get; set; } = StorageModel.DefaultName;
    [Required] public string ProviderType { get; set; } = "LocalDisk";

    public string? RootPath { get; set; }
    public int? MaxFileSizeMb { get; set; }
    public string? PublicUrlBase { get; set; }

    public string? Bucket { get; set; }
    public string? Region { get; set; }
    public string? ServiceUrl { get; set; }
    public bool ForcePathStyle { get; set; }
    public string? AccessKeyId { get; set; }

    /// <summary>The S3 secret access key. Never filled with the real value: the page shows the mask instead.</summary>
    public string? Secret { get; set; }
}

/// <summary>The storage configuration and the media browser. The page accepts bodies up to 60 MB so the service, not the web server, says a file is too large.</summary>
[RequestSizeLimit(60L * 1024 * 1024)]
public class StorageModel(ISharedServicesClient client, ILogger<StorageModel> logger) : PageModel
{
    public const string DefaultName = "Default storage";
    public const string Mask = "••••••••";
    public const int FileLimit = 60;

    public static readonly string[] Providers = ["LocalDisk", "S3", "Mock"];
    public static readonly string[] SuggestedProjects = ["default", "linguabridge", "identity"];

    [BindProperty(SupportsGet = true)] public string? Project { get; set; }

    /// <summary>"config" (the storage configuration) or "media" (the media browser): which tab is open.</summary>
    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }

    [BindProperty] public StorageForm Form { get; set; } = new();

    public IReadOnlyList<string> Projects { get; private set; } = SuggestedProjects;
    public IReadOnlyList<StoredFileDto> Files { get; private set; } = [];
    public IReadOnlyList<MediaLogDto> Logs { get; private set; } = [];

    /// <summary>The version the form was filled from: the NEWEST version of the configuration being edited, whatever its status (so "Save and activate" activates what is shown).</summary>
    public ChannelConfigurationResponse? Current { get; private set; }

    /// <summary>The version that is live now, when it is not the one shown (a newer draft is being edited).</summary>
    public ChannelConfigurationResponse? LiveVersion { get; private set; }

    public string? LoadError { get; private set; }

    public bool MediaTabOpen => string.Equals(Tab, "media", StringComparison.OrdinalIgnoreCase);

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var project = Normalize(Project);
        Project = project;
        try
        {
            var all = await client.ListConfigurationsAsync(null, "Storage", cancellationToken: cancellationToken);
            Projects = [.. SuggestedProjects.Concat(all.Select(c => c.ProjectCode)).Append(project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            var mine = all.Where(c => c.ProjectCode == project).ToList();
            var live = mine.Where(c => c.Status == "Active").OrderByDescending(c => c.ActivatedAt).FirstOrDefault();
            var chosen = live ?? mine.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
            Current = chosen is null ? null : mine.Where(c => c.Name == chosen.Name).OrderByDescending(c => c.Version).FirstOrDefault();
            LiveVersion = live is not null && Current is not null && live.Id != Current.Id ? live : null;
            Form = FormFrom(Current, project);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not load the storage configurations.");
            LoadError = UiHelpers.Describe(ex);
            Form = FormFrom(null, project);
        }

        try
        {
            Files = await client.ListFilesAsync(project, FileLimit, cancellationToken);
            Logs = await client.GetMediaLogsAsync(project, 20, cancellationToken);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not load the files or the media log.");
            LoadError ??= UiHelpers.Describe(ex);
        }
    }

    public Task<IActionResult> OnPostSaveDraftAsync(CancellationToken cancellationToken) => SaveAsync(activate: false, cancellationToken);

    public Task<IActionResult> OnPostSaveActivateAsync(CancellationToken cancellationToken) => SaveAsync(activate: true, cancellationToken);

    private async Task<IActionResult> SaveAsync(bool activate, CancellationToken cancellationToken)
    {
        var project = Normalize(Form.ProjectCode);
        try
        {
            var saved = await client.SaveConfigurationAsync(new SaveChannelConfigurationRequest(
                project, "Storage", Form.ProviderType, Form.Name.Trim(), BuildSettings(Form), SecretToSend(Form)), cancellationToken);

            if (activate)
            {
                var activated = await client.ActivateConfigurationAsync(saved.Id, cancellationToken);
                Message = $"Đã lưu và kích hoạt {activated.Name} phiên bản {activated.Version}.";
            }
            else
            {
                Message = $"Đã lưu bản nháp {saved.Name} phiên bản {saved.Version}. Form đang hiển thị đúng bản nháp này; bấm \"Lưu & Kích Hoạt\" để dùng các giá trị đó.";
            }
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not save the storage configuration for {Project}.", project);
            ErrorMessage = Describe(ex);
        }

        return RedirectToPage(new { project });
    }

    /// <summary>
    /// "Kiểm Tra Kết Nối". Called by the page's script with the anti-forgery token in a header; answers JSON. Unsaved values are tried from
    /// memory (nothing is stored). If the S3 secret was not retyped, the saved version is tested instead, but only while the settings on the
    /// screen are the saved ones: a stored secret access key is never used with settings that were not saved.
    /// </summary>
    public async Task<IActionResult> OnPostSendTestAsync(CancellationToken cancellationToken)
    {
        var project = Normalize(Form.ProjectCode);
        try
        {
            var secret = SecretToSend(Form);
            var settings = BuildSettings(Form);
            Guid? savedId = null;

            if (secret is null && Form.ProviderType == "S3")
            {
                var saved = await FindSavedAsync(project, Form.Name.Trim(), cancellationToken);
                if (saved is null || saved.ProviderType != Form.ProviderType || !saved.HasSecret)
                    return Result(false, "Nhập Secret Access Key để kiểm tra kết nối (chưa có bản đã lưu dùng cùng nhà cung cấp).");
                if (Canonical(saved.Settings) != Canonical(settings))
                    return Result(false, "Bạn đã đổi cài đặt. Hãy nhập lại Secret Access Key để thử các giá trị chưa lưu, hoặc lưu bản nháp rồi thử lại.");
                savedId = saved.Id;
            }

            var outcome = await client.TestStorageAsync(savedId is { } id
                ? new StorageTestRequest(ConfigurationId: id)
                : new StorageTestRequest(ProviderType: Form.ProviderType, Settings: settings, Secret: secret), cancellationToken);

            var suffix = outcome.UsedSavedConfiguration ? " (đã thử cấu hình đã lưu)" : " (giá trị đang nhập, chưa lưu)";
            return Result(outcome.Success, outcome.Message + suffix, outcome.LatencyMs);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "The storage test could not be run.");
            return Result(false, Describe(ex));
        }
    }

    // ------------------------------------------------------------------------------------------------ media browser

    /// <summary>Quick upload. The type is decided by the service from the bytes, so a renamed file is refused there, not here.</summary>
    public async Task<IActionResult> OnPostUploadAsync(IFormFile? upload, bool isPublic, string? project, CancellationToken cancellationToken)
    {
        var target = Normalize(project);
        if (upload is null || upload.Length == 0)
        {
            ErrorMessage = "Chọn một tệp để tải lên.";
            return RedirectToPage(new { project = target, tab = "media" });
        }

        try
        {
            await using var content = upload.OpenReadStream();
            var uploaded = await client.UploadFileAsync(new UploadFileRequest(upload.FileName, upload.ContentType, isPublic, target), content, cancellationToken);
            Message = $"Đã tải lên {uploaded.File.OriginalFileName} ({uploaded.File.MimeType}, {FormatSize(uploaded.File.FileSizeBytes)}).";
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not upload a file for {Project}.", target);
            ErrorMessage = Describe(ex);
        }

        return RedirectToPage(new { project = target, tab = "media" });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? project, CancellationToken cancellationToken)
    {
        var target = Normalize(project);
        try
        {
            Message = await client.DeleteFileAsync(id, cancellationToken) ? "Đã xóa tệp." : "Tệp không còn tồn tại.";
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not delete file {FileId}.", id);
            ErrorMessage = Describe(ex);
        }

        return RedirectToPage(new { project = target, tab = "media" });
    }

    /// <summary>The content of a file, streamed through the portal (the portal holds the API key; the browser holds none).</summary>
    public async Task<IActionResult> OnGetFileAsync(Guid id, bool download, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await client.DownloadFileAsync(id, cancellationToken);
            return Stream(stream, download);
        }
        catch (SharedServicesApiException ex) when (ex.StatusCode is 404 or 403)
        {
            return NotFound();
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not read file {FileId}.", id);
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    public async Task<IActionResult> OnGetThumbAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await client.GetThumbnailAsync(id, cancellationToken);
            return stream is null ? NotFound() : Stream(stream, download: false);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not read the thumbnail of {FileId}.", id);
            return NotFound();
        }
    }

    private IActionResult Stream(FileStreamResponse file, bool download)
    {
        Response.RegisterForDispose(file);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, max-age=300";
        if (!string.IsNullOrEmpty(file.ETag)) Response.Headers.ETag = file.ETag;
        return download
            ? File(file.Content, file.ContentType, file.FileName)
            : new FileStreamResult(file.Content, file.ContentType);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static JsonResult Result(bool success, string message, int latencyMs = 0) => new(new { success, message, latencyMs });

    /// <summary>The newest version of the series the form is about: the version the form shows, so "unchanged" means unchanged from that one.</summary>
    private async Task<ChannelConfigurationResponse?> FindSavedAsync(string project, string name, CancellationToken cancellationToken) =>
        (await client.ListConfigurationsAsync(project, "Storage", cancellationToken: cancellationToken))
            .Where(c => c.Name == name)
            .OrderByDescending(c => c.Version)
            .FirstOrDefault();

    private static string Normalize(string? project) => string.IsNullOrWhiteSpace(project) ? "default" : project.Trim().ToLowerInvariant();

    private static bool IsApiProblem(Exception ex) => ex is SharedServicesApiException or HttpRequestException or TaskCanceledException;

    /// <summary>The mask the form echoes back, and a blank, both mean "keep the stored secret".</summary>
    private static string? SecretToSend(StorageForm form) => string.IsNullOrEmpty(form.Secret) || form.Secret == Mask ? null : form.Secret;

    private static string Describe(Exception ex) => ex is SharedServicesApiException { Errors.Count: > 0 } api
        ? string.Join(" ", api.Errors.SelectMany(e => e.Value))
        : UiHelpers.Describe(ex);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    public static JsonElement BuildSettings(StorageForm form)
    {
        var settings = new SortedDictionary<string, object>(StringComparer.Ordinal);
        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) settings[key] = value.Trim();
        }

        switch (form.ProviderType)
        {
            case "LocalDisk":
                Put("rootPath", form.RootPath);
                Put("publicUrlBase", form.PublicUrlBase);
                if (form.MaxFileSizeMb is { } local) settings["maxFileSizeMb"] = local;
                break;
            case "S3":
                Put("bucket", form.Bucket);
                Put("region", form.Region);
                Put("serviceUrl", form.ServiceUrl);
                Put("accessKeyId", form.AccessKeyId);
                Put("publicUrlBase", form.PublicUrlBase);
                if (form.ForcePathStyle) settings["forcePathStyle"] = true;
                if (form.MaxFileSizeMb is { } s3) settings["maxFileSizeMb"] = s3;
                break;
        }

        return JsonSerializer.SerializeToElement(settings);
    }

    private static StorageForm FormFrom(ChannelConfigurationResponse? current, string project)
    {
        var form = new StorageForm { ProjectCode = project };
        if (current is null) return form;

        string? Text(string name) =>
            current.Settings.ValueKind == JsonValueKind.Object && current.Settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        form.Name = current.Name;
        form.ProviderType = current.ProviderType;
        form.RootPath = Text("rootPath");
        form.PublicUrlBase = Text("publicUrlBase");
        form.Bucket = Text("bucket");
        form.Region = Text("region");
        form.ServiceUrl = Text("serviceUrl");
        form.AccessKeyId = Text("accessKeyId");
        if (current.Settings.ValueKind == JsonValueKind.Object)
        {
            if (current.Settings.TryGetProperty("maxFileSizeMb", out var mb) && mb.ValueKind == JsonValueKind.Number && mb.TryGetInt32(out var parsed)) form.MaxFileSizeMb = parsed;
            form.ForcePathStyle = current.Settings.TryGetProperty("forcePathStyle", out var fps) && fps.ValueKind == JsonValueKind.True;
        }

        // The real secret never reaches the page; the mask stands for "a secret is stored".
        form.Secret = current.HasSecret ? Mask : null;
        return form;
    }

    /// <summary>The settings with their keys in order, to tell whether two sets are the same whatever the order they were written in.</summary>
    private static string Canonical(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return element.GetRawText();
        var sorted = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        return JsonSerializer.Serialize(sorted);
    }
}
