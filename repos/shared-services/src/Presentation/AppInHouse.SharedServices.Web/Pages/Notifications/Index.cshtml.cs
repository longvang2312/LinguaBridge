using System.ComponentModel.DataAnnotations;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages.Notifications;

/// <summary>What the "send a notification" form posts.</summary>
public sealed class DispatchForm
{
    [StringLength(50)] public string ProjectCode { get; set; } = "default";
    [Required] public string TemplateCode { get; set; } = string.Empty;
    [StringLength(254)] public string? RecipientEmail { get; set; }
    [StringLength(200)] public string? RecipientChatId { get; set; }

    /// <summary>One <c>Name=value</c> per line: the values of the template's placeholders.</summary>
    public string? DataText { get; set; }

    /// <summary>Queue the messages (retried by the service) instead of sending them now.</summary>
    public bool Queue { get; set; }
}

public class IndexModel(ISharedServicesClient client, ILogger<IndexModel> logger) : PageModel
{
    public sealed record NotificationTemplateItem(
        string Code, string Title, string Channels, string Subject, IReadOnlyList<string> Placeholders, string Priority, bool IsActive, int UsageCount);

    public sealed record NotificationDispatchLog(string EventType, string Project, string Recipient, string ChannelUsed, string Status, int LatencyMs, DateTimeOffset DispatchedAt);

    [BindProperty] public DispatchForm Form { get; set; } = new();

    public IReadOnlyList<NotificationTemplateItem> Templates { get; private set; } = [];
    public IReadOnlyList<NotificationDispatchLog> RecentDispatches { get; private set; } = [];
    public int ActiveTemplatesCount => Templates.Count(t => t.IsActive);
    public int TotalDispatchesToday { get; private set; }

    /// <summary>The share of the recent dispatches that reached somebody; null when there are none.</summary>
    public double? SuccessRate { get; private set; }

    public string? LoadError { get; private set; }
    public string? FormError { get; private set; }

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostDispatchAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(Form.TemplateCode))
        {
            FormError = "Hãy chọn mẫu thông báo.";
            await LoadAsync(cancellationToken);
            return Page();
        }

        var data = ParseData(Form.DataText);
        try
        {
            var result = await client.DispatchNotificationAsync(new DispatchNotificationRequest(
                Form.TemplateCode.Trim(), data, NullIfEmpty(Form.RecipientEmail), NullIfEmpty(Form.RecipientChatId),
                (Form.ProjectCode ?? "default").Trim().ToLowerInvariant(), Queue: Form.Queue ? true : null), cancellationToken);

            Message = result.Status switch
            {
                "Queued" => $"Đã đưa thông báo {Form.TemplateCode} vào hàng đợi ({result.JobIds.Count} tác vụ).",
                "Fallback" => $"Kênh chính lỗi, đã gửi bản tóm tắt qua kênh dự phòng ({string.Join(", ", result.Channels.Select(c => c.Channel))}).",
                "Partial" => "Chỉ một trong hai kênh gửi được: " + string.Join("; ", result.Channels.Where(c => !c.Success).Select(c => $"{c.Channel}: {c.Error}")),
                _ => $"Đã gửi thông báo {Form.TemplateCode} ({result.LatencyMs} ms).",
            };
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not dispatch the notification {Template}.", Form.TemplateCode);
            FormError = ex is SharedServicesApiException { Errors.Count: > 0 } api
                ? string.Join(" ", api.Errors.SelectMany(e => e.Value))
                : UiHelpers.Describe(ex);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<NotificationLogDto> logs = [];
        try
        {
            logs = await client.GetNotificationLogsAsync(limit: 100, cancellationToken: cancellationToken);
            var templates = await client.GetNotificationTemplatesAsync(cancellationToken);
            var usage = logs.GroupBy(l => l.TemplateCode).ToDictionary(g => g.Key, g => g.Count());
            Templates =
            [
                .. templates.Select(t => new NotificationTemplateItem(
                    t.Code, t.Name, ChannelText(t.DefaultChannel), t.SubjectTemplate, t.Placeholders, t.Priority, t.IsActive, usage.GetValueOrDefault(t.Code)))
            ];
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not load the notification templates and logs.");
            LoadError = UiHelpers.Describe(ex);
        }

        RecentDispatches =
        [
            .. logs.Take(15).Select(l => new NotificationDispatchLog(l.TemplateCode, l.ProjectCode, l.Recipient ?? "—", l.Channels, l.Status, l.LatencyMs, l.CreatedAt))
        ];
        var today = DateTimeOffset.UtcNow.Date;
        TotalDispatchesToday = logs.Count(l => l.CreatedAt.UtcDateTime.Date == today);
        var decided = logs.Where(l => l.Status != "Queued").ToList();
        SuccessRate = decided.Count == 0 ? null : Math.Round(100.0 * decided.Count(l => l.Status != "Failed") / decided.Count, 1);
    }

    private static string ChannelText(string channel) => channel switch
    {
        "Both" => "Email + Chat",
        _ => channel,
    };

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Reads <c>Name=value</c> lines; a line without '=' is ignored.</summary>
    private static Dictionary<string, string>? ParseData(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            values[line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        return values.Count == 0 ? null : values;
    }
}
