using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>What the email form posts. Fields of a provider other than the chosen one are ignored.</summary>
public sealed class EmailForm
{
    [Required, StringLength(50)] public string ProjectCode { get; set; } = "default";
    [Required, StringLength(100)] public string Name { get; set; } = EmailModel.DefaultName;
    [Required] public string ProviderType { get; set; } = "Smtp";

    public string? SmtpHost { get; set; }
    public string? SmtpPort { get; set; } = "587";
    public string? SmtpSecurity { get; set; } = "Auto";
    public string? SmtpUsername { get; set; }

    public string? SenderEmail { get; set; }
    public string? SenderName { get; set; }

    /// <summary>The SMTP password or the SendGrid API key. Never filled with the real value: the page shows the mask instead.</summary>
    public string? Secret { get; set; }

    public string? TestRecipient { get; set; }
}

public class EmailModel(ISharedServicesClient client, ILogger<EmailModel> logger) : PageModel
{
    public const string DefaultName = "Default email";
    public const string Mask = "••••••••";

    public static readonly string[] Providers = ["Smtp", "SendGrid", "Mock"];
    public static readonly string[] SuggestedProjects = ["default", "linguabridge", "identity"];
    public static readonly string[] SmtpSecurityOptions = ["Auto", "StartTls", "SslOnConnect", "None"];

    [BindProperty(SupportsGet = true)] public string? Project { get; set; }

    [BindProperty] public EmailForm Form { get; set; } = new();

    public IReadOnlyList<string> Projects { get; private set; } = SuggestedProjects;
    public IReadOnlyList<DeliveryLogDto> Logs { get; private set; } = [];

    /// <summary>
    /// The version the form was filled from: the NEWEST version of the configuration being edited, whatever its status. A draft that was
    /// just saved is therefore what the form shows next, so "Save and activate" activates exactly those values. (Showing the active
    /// version instead would put old settings next to the newer draft's secret, which a new version inherits.)
    /// </summary>
    public ChannelConfigurationResponse? Current { get; private set; }

    /// <summary>The version that is live now, when it is not the one shown (a newer draft is being edited).</summary>
    public ChannelConfigurationResponse? LiveVersion { get; private set; }

    public string? LoadError { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var project = Normalize(Project);
        Project = project;
        try
        {
            var all = await client.ListConfigurationsAsync(channelType: "Email", cancellationToken: cancellationToken);
            Projects = [.. SuggestedProjects.Concat(all.Select(c => c.ProjectCode)).Append(project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            var mine = all.Where(c => c.ProjectCode == project).ToList();
            // The configuration being edited is the one that is live (the one most recently activated), or else the newest one made.
            var live = mine.Where(c => c.Status == "Active").OrderByDescending(c => c.ActivatedAt).FirstOrDefault();
            var name = live?.Name ?? mine.OrderByDescending(c => c.CreatedAt).FirstOrDefault()?.Name;
            Current = mine.Where(c => c.Name == name).OrderByDescending(c => c.Version).FirstOrDefault();
            LiveVersion = live is not null && Current is not null && live.Id != Current.Id ? live : null;
            Form = FormFrom(Current, project);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not load the email configurations.");
            LoadError = UiHelpers.Describe(ex);
            Form = FormFrom(null, project);
        }

        await LoadLogsAsync(project, cancellationToken);
    }

    public Task<IActionResult> OnPostSaveDraftAsync(CancellationToken cancellationToken) => SaveAsync(activate: false, cancellationToken);

    public Task<IActionResult> OnPostSaveActivateAsync(CancellationToken cancellationToken) => SaveAsync(activate: true, cancellationToken);

    private async Task<IActionResult> SaveAsync(bool activate, CancellationToken cancellationToken)
    {
        var project = Normalize(Form.ProjectCode);
        try
        {
            var saved = await client.SaveConfigurationAsync(new SaveChannelConfigurationRequest(
                project, "Email", Form.ProviderType, Form.Name.Trim(), BuildSettings(Form), SecretToSend(Form)), cancellationToken);

            if (activate)
            {
                var activated = await client.ActivateConfigurationAsync(saved.Id, cancellationToken);
                Message = $"Đã lưu và kích hoạt {activated.Name} phiên bản {activated.Version}.";
            }
            else
            {
                Message = $"Đã lưu bản nháp {saved.Name} phiên bản {saved.Version}. Form đang hiển thị đúng bản nháp này; bấm \"Lưu & Kích hoạt\" để dùng các giá trị đó.";
            }
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not save the email configuration for {Project}.", project);
            ErrorMessage = Describe(ex);
        }

        return RedirectToPage(new { project });
    }

    /// <summary>
    /// "Gửi Thử Nghiệm". Called by the page's script with the anti-forgery token in a header; answers JSON. Unsaved values are tried from
    /// memory (nothing is stored). If the secret was not retyped, the saved version is tested instead, but only while the settings on the
    /// screen are the saved ones: a stored secret is never used with settings that were not saved.
    /// </summary>
    public async Task<IActionResult> OnPostSendTestAsync(CancellationToken cancellationToken)
    {
        var project = Normalize(Form.ProjectCode);
        if (string.IsNullOrWhiteSpace(Form.TestRecipient))
            return Result(false, "Nhập email người nhận thử nghiệm.");

        try
        {
            var secret = SecretToSend(Form);
            var settings = BuildSettings(Form);
            var needsSecret = Form.ProviderType is "SendGrid" or "Brevo";
            Guid? savedId = null;

            if (secret is null && needsSecret)
            {
                var saved = await FindSavedAsync(project, Form.Name.Trim(), cancellationToken);
                if (saved is null || saved.ProviderType != Form.ProviderType)
                    return Result(false, "Nhập API key để gửi thử (chưa có bản đã lưu dùng cùng nhà cung cấp).");
                if (Canonical(saved.Settings) != Canonical(settings))
                    return Result(false, "Bạn đã đổi cài đặt. Hãy nhập lại API key để thử các giá trị chưa lưu, hoặc lưu bản nháp rồi thử lại.");
                savedId = saved.Id;
            }
            else if (secret is null && Form.ProviderType == "Smtp" && !string.IsNullOrWhiteSpace(Form.SmtpUsername))
            {
                var saved = await FindSavedAsync(project, Form.Name.Trim(), cancellationToken);
                if (saved is { HasSecret: true } && saved.ProviderType == "Smtp" && Canonical(saved.Settings) == Canonical(settings)) savedId = saved.Id;
                else return Result(false, "Nhập mật khẩu SMTP để gửi thử các giá trị chưa lưu (mật khẩu đã lưu không được dùng với cài đặt mới).");
            }

            var outcome = await client.SendTestEmailAsync(savedId is { } id
                ? new SendTestEmailRequest(Form.TestRecipient.Trim(), ConfigurationId: id)
                : new SendTestEmailRequest(Form.TestRecipient.Trim(), ProviderType: Form.ProviderType, Settings: settings, Secret: secret), cancellationToken);

            var suffix = outcome.UsedSavedConfiguration ? " (đã thử cấu hình đã lưu)" : " (giá trị đang nhập, chưa lưu)";
            return Result(outcome.Success, outcome.Message + suffix, outcome.LatencyMs);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "The email test could not be run.");
            return Result(false, Describe(ex));
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static JsonResult Result(bool success, string message, int latencyMs = 0) =>
        new(new { success, message, latencyMs });

    private async Task LoadLogsAsync(string project, CancellationToken cancellationToken)
    {
        try
        {
            Logs = await client.GetEmailDeliveryLogsAsync(project, 20, cancellationToken);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not load the email delivery logs.");
            LoadError ??= UiHelpers.Describe(ex);
        }
    }

    private async Task<ChannelConfigurationResponse?> FindSavedAsync(string project, string name, CancellationToken cancellationToken) =>
        (await client.ListConfigurationsAsync(project, "Email", cancellationToken: cancellationToken))
            .Where(c => c.Name == name)
            .OrderByDescending(c => c.Version)   // the version the form shows (see Current), so "unchanged" means unchanged from that one
            .FirstOrDefault();

    private static string Normalize(string? project) => string.IsNullOrWhiteSpace(project) ? "default" : project.Trim().ToLowerInvariant();

    private static bool IsApiProblem(Exception ex) => ex is SharedServicesApiException or HttpRequestException or TaskCanceledException;

    /// <summary>The mask the form echoes back, and a blank, both mean "keep the stored secret".</summary>
    private static string? SecretToSend(EmailForm form) =>
        string.IsNullOrEmpty(form.Secret) || form.Secret == Mask ? null : form.Secret;

    private static string Describe(Exception ex) => ex is SharedServicesApiException { Errors.Count: > 0 } api
        ? string.Join(" ", api.Errors.SelectMany(e => e.Value))
        : UiHelpers.Describe(ex);

    public static JsonElement BuildSettings(EmailForm form)
    {
        var settings = new SortedDictionary<string, object>(StringComparer.Ordinal);
        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) settings[key] = value.Trim();
        }

        if (form.ProviderType == "Smtp")
        {
            Put("host", form.SmtpHost);
            settings["port"] = int.TryParse(form.SmtpPort, out var port) ? port : 587;
            Put("useSsl", form.SmtpSecurity);
            Put("username", form.SmtpUsername);
        }

        Put("senderEmail", form.SenderEmail);
        Put("senderName", form.SenderName);
        return JsonSerializer.SerializeToElement(settings);
    }

    private static EmailForm FormFrom(ChannelConfigurationResponse? current, string project)
    {
        var form = new EmailForm { ProjectCode = project };
        if (current is null) return form;

        string? Text(string name) =>
            current.Settings.ValueKind == JsonValueKind.Object && current.Settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        form.Name = current.Name;
        form.ProviderType = current.ProviderType;
        form.SmtpHost = Text("host");
        form.SmtpSecurity = Text("useSsl") ?? "Auto";
        form.SmtpUsername = Text("username");
        form.SenderEmail = Text("senderEmail");
        form.SenderName = Text("senderName");
        if (current.Settings.ValueKind == JsonValueKind.Object && current.Settings.TryGetProperty("port", out var port) && port.TryGetInt32(out var number))
            form.SmtpPort = number.ToString();

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
