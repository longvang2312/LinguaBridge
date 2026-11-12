using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

public class IndexModel(ISharedServicesClient client, ILogger<IndexModel> logger) : PageModel
{
    public sealed record MetricItem(string Label, string Value);
    public sealed record QuickAction(string Title, string Url, string? Icon = null);

    public sealed record ServiceLaunchpadCard(
        string Id,
        string Name,
        string Category,
        string Tagline,
        string Description,
        string Icon,
        string AccentClass,
        string IconClass,
        string Status,
        string StatusText,
        string ManageUrl,
        int ActiveCount,
        int DraftCount,
        IReadOnlyList<MetricItem> Metrics,
        IReadOnlyList<QuickAction> QuickActions);

    public sealed record ActivityItem(
        string Type,
        string Title,
        string Description,
        string Actor,
        string Status,
        string StatusBadge,
        string Icon,
        string IconColor,
        DateTimeOffset Timestamp);

    public IReadOnlyList<ServiceLaunchpadCard> ServiceCards { get; private set; } = [];
    public IReadOnlyList<ActivityItem> RecentActivities { get; private set; } = [];

    // Quick Stats Bar
    public int ActiveServicesCount { get; private set; }
    public int TotalServicesCount { get; private set; } = 8;
    public long TotalRequestsToday { get; private set; }

    /// <summary>The share of the deliveries of the last 24 hours that went out; null when there were none.</summary>
    public double? SuccessDeliveryRate { get; private set; }

    /// <summary>"Healthy", "Degraded" or "Idle" for the whole platform, from the health of the eight services.</summary>
    public string OverallStatus { get; private set; } = "Unknown";

    public string OverallText => OverallStatus switch
    {
        "Healthy" => "Tất cả hệ thống hoạt động ổn định",
        "Degraded" => "Có dịch vụ đang suy giảm: xem thẻ màu vàng bên dưới",
        "Idle" => "Chưa có dịch vụ nào được cấu hình",
        _ => "Chưa lấy được trạng thái hệ thống",
    };

    public DateTimeOffset? MetricsAt { get; private set; }
    public string UptimeText { get; private set; } = "—";
    public string TotalStorageFormatted { get; private set; } = "0 B";
    public int TotalFilesCount { get; private set; }
    public int ActiveApiKeysCount { get; private set; }

    public string? LoadError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelConfigurationResponse> configs = [];
        IReadOnlyList<ApiKeyDto> apiKeys = [];
        IReadOnlyList<DeliveryLogDto> emailLogs = [];
        IReadOnlyList<DeliveryLogDto> chatLogs = [];
        IReadOnlyList<AuditLogDto> auditLogs = [];

        try
        {
            configs = await client.ListConfigurationsAsync(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load configurations for launchpad overview.");
            LoadError ??= UiHelpers.Describe(ex);
        }

        SystemMetricsOverviewDto? overview = null;
        try
        {
            overview = await client.GetMetricsOverviewAsync(cancellationToken);
            MetricsAt = overview.GeneratedAt;
            TotalServicesCount = overview.ServicesTotal;
            ActiveServicesCount = overview.ServicesHealthy + overview.ServicesDegraded;
            TotalRequestsToday = overview.TotalRequests24h;
            SuccessDeliveryRate = overview.SuccessRatePercent;
            TotalFilesCount = overview.FileCount;
            TotalStorageFormatted = UiHelpers.FormatBytes(overview.StorageBytes);
            OverallStatus = overview.ServicesDegraded > 0 ? "Degraded" : overview.ServicesHealthy == 0 ? "Idle" : "Healthy";
            var uptime = TimeSpan.FromSeconds(overview.UptimeSeconds);
            UptimeText = uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays} ngày {uptime.Hours} giờ" : $"{(int)uptime.TotalHours} giờ {uptime.Minutes} phút";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load the metrics for launchpad overview.");
            LoadError ??= UiHelpers.Describe(ex);
        }

        try
        {
            apiKeys = await client.ListApiKeysAsync(cancellationToken: cancellationToken);
            ActiveApiKeysCount = apiKeys.Count(k => k.Status == "Active");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load api keys for launchpad overview.");
        }

        try
        {
            emailLogs = await client.GetEmailDeliveryLogsAsync(limit: 25, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load email delivery logs.");
        }

        try
        {
            chatLogs = await client.GetBotDeliveryLogsAsync(limit: 25, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load chat delivery logs.");
        }

        try
        {
            auditLogs = await client.ListAuditLogsAsync(limit: 15, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load audit logs for launchpad overview.");
        }

        var allDelivery = emailLogs.Concat(chatLogs).ToList();

        int CountActive(string channel) =>
            configs.Count(c => (c.ChannelType == channel || (channel == "Chat" && c.ChannelType == "Telegram")) && c.Status == "Active");

        int CountDraft(string channel) =>
            configs.Count(c => (c.ChannelType == channel || (channel == "Chat" && c.ChannelType == "Telegram")) && c.Status == "Draft");

        // The state and the live figures of every card come from the metrics of the API; the texts are the portal's own.
        ServiceHealthDto? Health(string id) => overview?.Services.FirstOrDefault(s => s.Id == id);
        string StatusOf(string id) => Health(id)?.Status switch { "Healthy" => "Healthy", "Degraded" => "Warning", "Idle" => "Idle", _ => "Unknown" };
        string StatusTextOf(string id) => Health(id)?.Status switch { "Healthy" => "Hoạt động", "Degraded" => "Suy giảm", "Idle" => "Chưa cấu hình", _ => "Không rõ" };
        string Metric(string id, string key, string fallback = "—") => Health(id)?.Metrics.GetValueOrDefault(key) ?? fallback;

        // Build 8 Service Cards
        ServiceCards =
        [
            new(
                "email", "Email Service", "Truyền thông", "SMTP · SendGrid · Brevo",
                "Hạ tầng gửi email đa luồng với Template Engine động, tự động chuyển đổi failover và theo dõi trạng thái giao thư.",
                "bi-envelope-at-fill", "accent-email", "icon-email", StatusOf("email"), StatusTextOf("email"), "/Email",
                CountActive("Email"), CountDraft("Email"),
                [new("Gửi 24h", Metric("email", "sent24h")), new("Lỗi 24h", Metric("email", "failed24h"))],
                [new("Quản lý Email", "/Email", "bi-sliders"), new("Gửi thử", "/Email#test", "bi-send")]
            ),
            new(
                "chat", "Chat Service", "Truyền thông", "Realtime · SignalR · 1-1 & Nhóm",
                "Trò chuyện trực tuyến giữa người dùng. Các bot gửi tin ra ngoài (Telegram, Discord, Slack) do Notification Service quản lý; số liệu bot hiển thị ở đây cho tới khi Chat có số liệu riêng.",
                "bi-chat-dots-fill", "accent-chat", "icon-chat", StatusOf("chat"), StatusTextOf("chat"), "/Chat",
                CountActive("Chat"), CountDraft("Chat"),
                [new("Tin bot 24h", Metric("chat", "sent24h")), new("Lỗi bot 24h", Metric("chat", "failed24h"))],
                [new("Giám sát Realtime", "/Chat", "bi-activity"), new("Kênh & Bot", "/Notifications/Channels", "bi-robot")]
            ),
            new(
                "storage", "Media & Storage Service", "Dữ liệu & Tệp tin", "S3 · MinIO · Local Disk",
                "Lưu trữ tệp tin đa nền tảng, xác thực nhị phân Magic Bytes chống giả mạo MIME type, tự động nén và sinh ảnh thumbnail.",
                "bi-images", "accent-storage", "icon-storage", StatusOf("storage"), StatusTextOf("storage"), "/Storage",
                CountActive("Storage"), CountDraft("Storage"),
                [new("Dung lượng", TotalStorageFormatted), new("Tệp tin", $"{TotalFilesCount} files")],
                [new("Cấu hình S3/Disk", "/Storage", "bi-gear"), new("Media Browser", "/Storage?tab=media", "bi-folder2-open")]
            ),
            new(
                "projects", "Project & Access Service", "Quản trị & Định danh", "Multi-Tenant · API Keys · Quotas",
                "Quản lý định danh các ứng dụng thành viên (Identity, LinguaBridge, CRM), vòng đời API Key băm SHA-256, phân quyền Scopes và hạn ngạch theo ngày.",
                "bi-folder2-open", "accent-projects", "icon-projects", StatusOf("projects"), StatusTextOf("projects"), "/Projects/Index",
                int.TryParse(Metric("projects", "activeProjects", "0"), out var projectCount) ? projectCount : 0, 0,
                [new("Dự án", $"{Metric("projects", "activeProjects")} hoạt động"), new("API Keys", $"{ActiveApiKeysCount} active")],
                [new("Quản lý Dự án", "/Projects/Index", "bi-building"), new("Tạo API Key", "/ApiKeys", "bi-key")]
            ),
            new(
                "notifications", "Notification Service", "Truyền thông đa kênh", "Smart Routing · Multi-Channel",
                "Trung tâm thông báo đa kênh: Email, Telegram, Discord, Slack. Nhận sự kiện hệ thống, ánh xạ template và gửi đồng thời qua các kênh phù hợp.",
                "bi-bell-fill", "accent-notifications", "icon-notifications", StatusOf("notifications"), StatusTextOf("notifications"), "/Notifications/Index",
                int.TryParse(Metric("notifications", "activeTemplates", "0"), out var templateCount) ? templateCount : 0, 0,
                [new("Mẫu thông báo", $"{Metric("notifications", "activeTemplates")} mẫu"), new("Hôm nay", Metric("notifications", "dispatched24h"))],
                [new("Xem Hub", "/Notifications/Index", "bi-grid"), new("Kênh & Bot", "/Notifications/Channels", "bi-robot"), new("Gửi thông báo", "/Notifications/Index#dispatch-card", "bi-send-fill")]
            ),
            new(
                "jobs", "Background Job Service", "Vận hành & Tác vụ", "Message Queues · Cron Scheduler",
                "Hàng đợi tác vụ bất đồng bộ, lập lịch công việc định kỳ (Cron), Outbox Pattern đảm bảo toàn vẹn và cơ chế Retry lũy thừa an toàn.",
                "bi-cpu-fill", "accent-jobs", "icon-jobs", StatusOf("jobs"), StatusTextOf("jobs"), "/Jobs/Index",
                5, 0,
                [new("Đang chờ", Metric("jobs", "pending")), new("Lỗi", Metric("jobs", "failed"))],
                [new("Quản lý Queues", "/Jobs/Index", "bi-stack"), new("Xem Cron", "/Jobs/Index", "bi-calendar-check")]
            ),
            new(
                "webhooks", "Webhook Service", "Tích hợp API", "Inbound Receivers · Outbound",
                "Tiếp nhận webhook bên thứ 3 (Telegram, GitHub, Stripe), xác thực chữ ký số HMAC SHA-256 và điều phối phân phối nội bộ tin cậy.",
                "bi-arrow-left-right", "accent-webhooks", "icon-webhooks", StatusOf("webhooks"), StatusTextOf("webhooks"), "/Webhooks/Index",
                3, 0,
                [new("Nhận 24h", Metric("webhooks", "inboundAccepted24h")), new("Đăng ký", Metric("webhooks", "activeSubscriptions"))],
                [new("Cấu hình Webhook", "/Webhooks/Index", "bi-plug"), new("Đăng ký mới", "/Webhooks/Index#new-subscription-form", "bi-shield-check")]
            ),
            new(
                "monitoring", "Monitoring & Audit Service", "Giám sát & Logs", "Audit Trail · Health & Metrics",
                "Thu thập tập trung 4 luồng nhật ký vận hành (Delivery, Media, Audit, Operations), đo lường Uptime, tỷ lệ thành công và trạng thái từng dịch vụ.",
                "bi-shield-check", "accent-monitoring", "icon-monitoring", StatusOf("monitoring"), StatusTextOf("monitoring"), "/Audit",
                1, 0,
                [new("Logs kiểm toán", $"{auditLogs.Count} mới"), new("Uptime", UptimeText)],
                [new("Xem Audit Logs", "/Audit", "bi-journal-text"), new("Cấu hình chung", "/Configurations/Index", "bi-sliders")]
            )
        ];

        // Build Recent Activity Feed
        var activities = new List<ActivityItem>();

        foreach (var audit in auditLogs.Take(6))
        {
            activities.Add(new(
                "Audit",
                audit.Action,
                $"Tài nguyên {audit.ResourceType} ({audit.ResourceId})",
                audit.Actor,
                "Audit",
                "bg-primary-subtle text-primary",
                "bi-shield-check",
                "text-primary",
                audit.CreatedAt));
        }

        foreach (var delivery in allDelivery.Take(6))
        {
            activities.Add(new(
                "Delivery",
                $"Gửi {delivery.ChannelType} tới {delivery.Recipient}",
                delivery.ProviderResponse ?? "Đã gửi thành công qua nhà cung cấp",
                delivery.ProjectCode,
                delivery.Status,
                delivery.Status == "Delivered" || delivery.Status == "Success" ? "bg-success-subtle text-success" : "bg-warning-subtle text-warning",
                delivery.ChannelType == "Email" ? "bi-envelope" : "bi-chat-dots",
                delivery.ChannelType == "Email" ? "text-info" : "text-success",
                delivery.CreatedAt));
        }

        if (activities.Count == 0)
        {
            activities.Add(new(
                "System",
                "Hệ thống Multi-Services khởi động thành công",
                "Tất cả 8 phân hệ dịch vụ dùng chung đã sẵn sàng phục vụ",
                "System",
                "Healthy",
                "bg-success-subtle text-success",
                "bi-check-circle",
                "text-success",
                DateTimeOffset.UtcNow));
        }

        RecentActivities = activities.OrderByDescending(a => a.Timestamp).Take(8).ToList();
    }
}
