using System.ComponentModel.DataAnnotations;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages.Webhooks;

/// <summary>What the "new subscription" form posts. An empty secret means the service makes one.</summary>
public sealed class SubscriptionForm
{
    [StringLength(50)] public string ProjectCode { get; set; } = "default";
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(500)] public string TargetUrl { get; set; } = string.Empty;
    public List<string> Events { get; set; } = [];
    public string? SecretKey { get; set; }
}

/// <summary>
/// The webhooks: what the service receives from third parties and the applications that are told about events. The signing key of a new
/// subscription is shown once, in the answer to the request that makes it (the page is returned, not redirected to, so it is never put in
/// a cookie), and the page is not cached.
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class IndexModel(ISharedServicesClient client, ILogger<IndexModel> logger) : PageModel
{
    public static readonly string[] Events = ["email.sent", "email.failed", "chat.sent", "chat.failed", "file.uploaded", "webhook.received"];

    [BindProperty] public SubscriptionForm Form { get; set; } = new();

    public IReadOnlyList<InboundSourceDto> InboundEndpoints { get; private set; } = [];
    public IReadOnlyList<WebhookSubscriptionDto> Subscriptions { get; private set; } = [];

    /// <summary>Set only on the response to creating a subscription: the one time its signing key is shown.</summary>
    public string? NewSecret { get; private set; }

    public WebhookSubscriptionDto? CreatedSubscription { get; private set; }

    public string? LoadError { get; private set; }
    public string? FormError { get; private set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            FormError = "Hãy nhập tên và địa chỉ nhận.";
        }
        else
        {
            try
            {
                var created = await client.CreateWebhookSubscriptionAsync(new CreateWebhookSubscriptionRequest(
                    Form.Name.Trim(), Form.TargetUrl.Trim(), Form.Events,
                    string.IsNullOrWhiteSpace(Form.SecretKey) ? null : Form.SecretKey.Trim(), (Form.ProjectCode ?? "default").Trim().ToLowerInvariant()), cancellationToken);
                NewSecret = created.SecretKey;
                CreatedSubscription = created.Subscription;
                Form = new SubscriptionForm { ProjectCode = created.Subscription.ProjectCode };
            }
            catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not create a webhook subscription.");
                FormError = ex is SharedServicesApiException { Errors.Count: > 0 } api ? string.Join(" ", api.Errors.SelectMany(e => e.Value)) : UiHelpers.Describe(ex);
            }
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteWebhookSubscriptionAsync(id, cancellationToken);
            Message = "Đã xóa đăng ký webhook.";
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not delete webhook subscription {Id}.", id);
            ErrorMessage = UiHelpers.Describe(ex);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.TestWebhookSubscriptionAsync(id, cancellationToken);
            if (result.Success) Message = $"Ứng dụng nhận đã trả lời {result.StatusCode} sau {result.LatencyMs} ms.";
            else ErrorMessage = $"Gửi thử không thành công: {result.Error ?? "không có phản hồi"}";
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not test webhook subscription {Id}.", id);
            ErrorMessage = UiHelpers.Describe(ex);
        }

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Subscriptions = await client.GetWebhookSubscriptionsAsync(cancellationToken);
            InboundEndpoints = await client.GetInboundWebhookSourcesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SharedServicesApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not load the webhooks.");
            LoadError = UiHelpers.Describe(ex);
        }
    }
}
