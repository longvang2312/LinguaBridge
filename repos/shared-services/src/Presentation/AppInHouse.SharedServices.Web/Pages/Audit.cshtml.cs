using System.Text.Encodings.Web;
using System.Text.Json;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>The audit log: who changed what, with the filters of the page. The details come cleaned of secrets from the service.</summary>
public class AuditModel(ISharedServicesClient client, ILogger<AuditModel> logger) : PageModel
{
    public const int PageSize = 100;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Part of the actor name (a user's email, or "apikey:aih_live_xxxxxx").</summary>
    [BindProperty(SupportsGet = true)] public string? Actor { get; set; }

    [BindProperty(SupportsGet = true)] public string? Action { get; set; }

    /// <summary>First day to show, in UTC.</summary>
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }

    /// <summary>Last day to show (the whole day), in UTC.</summary>
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }

    public IReadOnlyList<AuditLogDto> Logs { get; private set; } = [];

    /// <summary>The actions in the log, to fill the filter. Falls back to the known ones when the list cannot be read.</summary>
    public IReadOnlyList<string> Actions { get; private set; } = KnownActions;

    public static readonly string[] KnownActions = ["CreateConfig", "CreateVersion", "ActivateVersion", "RollbackVersion", "SendTest", "CreateApiKey", "RotateApiKey", "RevokeApiKey"];

    public string? LoadError { get; private set; }

    public string? FilterError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (From is { } from && To is { } to && to < from)
        {
            FilterError = "Ngày kết thúc phải sau hoặc trùng ngày bắt đầu.";
            return;
        }

        try
        {
            Actions = [.. KnownActions.Concat(await client.GetAuditActionsAsync(cancellationToken)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            var start = From is { } f ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : (DateTimeOffset?)null;
            // "To" is a whole day, and the service's end is exclusive: the end is the start of the next day.
            var end = To is { } t ? new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : (DateTimeOffset?)null;
            Logs = await client.ListAuditLogsAsync(Actor, Action, start, end, PageSize, cancellationToken);
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not load the audit log.");
            LoadError = UiHelpers.Describe(ex);
        }
    }

    /// <summary>The details as indented JSON for reading; anything that is not JSON is shown as it is.</summary>
    public static string PrettyDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            return JsonSerializer.Serialize(document.RootElement, Pretty);
        }
        catch (JsonException)
        {
            return detailsJson;
        }
    }
}
