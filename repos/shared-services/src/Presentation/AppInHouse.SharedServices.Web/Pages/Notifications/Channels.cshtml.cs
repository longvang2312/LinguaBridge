using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages.Notifications;

/// <summary>What the bot channel form posts. Fields of a provider other than the chosen one are ignored.</summary>
public sealed class BotChannelForm
{
    [Required, StringLength(50)] public string ProjectCode { get; set; } = "default";
    [Required, StringLength(100)] public string Name { get; set; } = ChannelsModel.DefaultName;
    [Required] public string ProviderType { get; set; } = "TelegramBot";

    /// <summary>"Chat", or "Telegram" for a configuration saved before Chat existed (a new version stays in the series it belongs to).</summary>
    public string ChannelType { get; set; } = "Chat";

    public string? TelegramChatId { get; set; }
    public string? TelegramParseMode { get; set; } = "PlainText";

    public string? DiscordUsername { get; set; }
    public string? DiscordAvatarUrl { get; set; }

    public string? SlackChannel { get; set; }
    public string? SlackIcon { get; set; }

    /// <summary>The bot token or the webhook address. Never filled with the real value: the page shows the mask instead.</summary>
    public string? Secret { get; set; }

    public string? TestRecipient { get; set; }
    public string? TestMessage { get; set; }
}

public class ChannelsModel(ISharedServicesClient client, ILogger<ChannelsModel> logger) : PageModel
{
    public const string DefaultName = "Default chat";
    public const string Mask = "••••••••";

    public static readonly string[] Providers = ["TelegramBot", "DiscordWebhook", "SlackWebhook", "Mock"];
    public static readonly string[] SuggestedProjects = ["default", "linguabridge", "identity"];
    public static readonly string[] ParseModes = ["PlainText", "MarkdownV2"];

    [BindProperty(SupportsGet = true)] public string? Project { get; set; }

    [BindProperty] public BotChannelForm Form { get; set; } = new();

    public IReadOnlyList<string> Projects { get; private set; } = SuggestedProjects;
    public IReadOnlyList<DeliveryLogDto> Logs { get; private set; } = [];

    /// <summary>
    /// The version the form was filled from: the NEWEST version of the configuration being edited, whatever its status. A draft that was
    /// just saved is therefore what the form shows next, so "Save and activate" activates exactly those values (a version made from the
    /// form inherits the secret of the newest version, so the form must show that one).
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
            var all = await ListChatAsync(null, cancellationToken);
            Projects = [.. SuggestedProjects.Concat(all.Select(c => c.ProjectCode)).Append(project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            var mine = all.Where(c => c.ProjectCode == project).ToList();
            // The configuration being edited is the one that is live (the one most recently activated), or else the newest one made.
            var live = mine.Where(c => c.Status == "Active").OrderByDescending(c => c.ActivatedAt).FirstOrDefault();
            var chosen = live ?? mine.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
            Current = chosen is null
                ? null
                : mine.Where(c => c.Name == chosen.Name && c.ChannelType == chosen.ChannelType).OrderByDescending(c => c.Version).FirstOrDefault();
            LiveVersion = live is not null && Current is not null && live.Id != Current.Id ? live : null;
            Form = FormFrom(Current, project);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not load the chat configurations.");
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
            var saved = await client.SaveNotificationChannelAsync(new SaveNotificationChannelRequest(
                project, Form.ProviderType, Form.Name.Trim(), ChannelOf(Form), BuildSettings(Form), SecretToSend(Form)), cancellationToken);

            if (activate)
            {
                var activated = await client.ActivateNotificationChannelAsync(saved.Id, cancellationToken);
                Message = $"Đã lưu và kích hoạt {activated.Name} phiên bản {activated.Version}.";
            }
            else
            {
                Message = $"Đã lưu bản nháp {saved.Name} phiên bản {saved.Version}. Form đang hiển thị đúng bản nháp này; bấm \"Lưu & Kích hoạt\" để dùng các giá trị đó.";
            }
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not save the chat configuration for {Project}.", project);
            ErrorMessage = Describe(ex);
        }

        return RedirectToPage(new { project });
    }

    /// <summary>
    /// "Gửi Thử Nghiệm". Called by the page's script with the anti-forgery token in a header; answers JSON. Unsaved values are tried from
    /// memory (nothing is stored). If the secret was not retyped, the saved version is tested instead, but only while the settings on the
    /// screen are the saved ones: a stored token or webhook address is never used with settings that were not saved.
    /// </summary>
    public async Task<IActionResult> OnPostSendTestAsync(CancellationToken cancellationToken)
    {
        var project = Normalize(Form.ProjectCode);
        try
        {
            var secret = SecretToSend(Form);
            var settings = BuildSettings(Form);
            Guid? savedId = null;

            if (secret is null && Form.ProviderType != "Mock")
            {
                var saved = await FindSavedAsync(project, Form.Name.Trim(), ChannelOf(Form), cancellationToken);
                if (saved is null || saved.ProviderType != Form.ProviderType || !saved.HasSecret)
                    return Result(false, $"Nhập {SecretLabel(Form.ProviderType)} để gửi thử (chưa có bản đã lưu dùng cùng nhà cung cấp).");
                if (Canonical(saved.Settings) != Canonical(settings))
                    return Result(false, $"Bạn đã đổi cài đặt. Hãy nhập lại {SecretLabel(Form.ProviderType)} để thử các giá trị chưa lưu, hoặc lưu bản nháp rồi thử lại.");
                savedId = saved.Id;
            }

            var recipient = string.IsNullOrWhiteSpace(Form.TestRecipient) ? null : Form.TestRecipient.Trim();
            var text = string.IsNullOrWhiteSpace(Form.TestMessage) ? null : Form.TestMessage;
            var outcome = await client.TestNotificationChannelAsync(savedId is { } id
                ? new TestBotChannelRequest(recipient, text, ConfigurationId: id)
                : new TestBotChannelRequest(recipient, text, ProviderType: Form.ProviderType, Settings: settings, Secret: secret), cancellationToken);

            var suffix = outcome.UsedSavedConfiguration ? " (đã thử cấu hình đã lưu)" : " (giá trị đang nhập, chưa lưu)";
            return Result(outcome.Success, outcome.Message + suffix, outcome.LatencyMs);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "The chat test could not be run.");
            return Result(false, Describe(ex));
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static JsonResult Result(bool success, string message, int latencyMs = 0) => new(new { success, message, latencyMs });

    /// <summary>The bot channels of a project: the ones saved as Chat and the ones saved before Chat existed, as Telegram.</summary>
    private Task<IReadOnlyList<ChannelConfigurationResponse>> ListChatAsync(string? project, CancellationToken cancellationToken) =>
        client.GetNotificationChannelsAsync(project, "bot", cancellationToken: cancellationToken);

    private async Task LoadLogsAsync(string project, CancellationToken cancellationToken)
    {
        try
        {
            Logs = await client.GetBotDeliveryLogsAsync(project, 20, cancellationToken);
        }
        catch (Exception ex) when (IsApiProblem(ex))
        {
            logger.LogWarning(ex, "Could not load the chat delivery logs.");
            LoadError ??= UiHelpers.Describe(ex);
        }
    }

    /// <summary>The newest version of the series the form is about: the version the form shows, so "unchanged" means unchanged from that one.</summary>
    private async Task<ChannelConfigurationResponse?> FindSavedAsync(string project, string name, string channel, CancellationToken cancellationToken) =>
        (await client.GetNotificationChannelsAsync(project, "bot", cancellationToken: cancellationToken))
            .Where(c => c.ChannelType == channel && c.Name == name)
            .OrderByDescending(c => c.Version)
            .FirstOrDefault();

    private static string ChannelOf(BotChannelForm form) => form.ChannelType == "Telegram" ? "Telegram" : "Chat";

    private static string SecretLabel(string provider) => provider == "TelegramBot" ? "Bot Token" : "Webhook URL";

    private static string Normalize(string? project) => string.IsNullOrWhiteSpace(project) ? "default" : project.Trim().ToLowerInvariant();

    private static bool IsApiProblem(Exception ex) => ex is SharedServicesApiException or HttpRequestException or TaskCanceledException;

    /// <summary>The mask the form echoes back, and a blank, both mean "keep the stored secret".</summary>
    private static string? SecretToSend(BotChannelForm form) => string.IsNullOrEmpty(form.Secret) || form.Secret == Mask ? null : form.Secret;

    private static string Describe(Exception ex) => ex is SharedServicesApiException { Errors.Count: > 0 } api
        ? string.Join(" ", api.Errors.SelectMany(e => e.Value))
        : UiHelpers.Describe(ex);

    public static JsonElement BuildSettings(BotChannelForm form)
    {
        var settings = new SortedDictionary<string, object>(StringComparer.Ordinal);
        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) settings[key] = value.Trim();
        }

        switch (form.ProviderType)
        {
            case "TelegramBot":
                Put("defaultChatId", form.TelegramChatId);
                Put("parseMode", form.TelegramParseMode);
                break;
            case "DiscordWebhook":
                Put("defaultUsername", form.DiscordUsername);
                Put("avatarUrl", form.DiscordAvatarUrl);
                break;
            case "SlackWebhook":
                Put("defaultChannel", form.SlackChannel);
                Put("iconEmoji", form.SlackIcon);
                break;
        }

        return JsonSerializer.SerializeToElement(settings);
    }

    private static BotChannelForm FormFrom(ChannelConfigurationResponse? current, string project)
    {
        var form = new BotChannelForm { ProjectCode = project };
        if (current is null) return form;

        string? Text(string name) =>
            current.Settings.ValueKind == JsonValueKind.Object && current.Settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        form.Name = current.Name;
        form.ChannelType = current.ChannelType == "Telegram" ? "Telegram" : "Chat";
        form.ProviderType = current.ProviderType;
        form.TelegramChatId = Text("defaultChatId");
        form.TelegramParseMode = Text("parseMode") ?? "PlainText";
        form.DiscordUsername = Text("defaultUsername");
        form.DiscordAvatarUrl = Text("avatarUrl");
        form.SlackChannel = Text("defaultChannel");
        form.SlackIcon = Text("iconEmoji");

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
