using AppInHouse.SharedServices.ClientSdk;

namespace AppInHouse.SharedServices.Web;

public static class UiHelpers
{
    public static string StatusBadge(string status) => status switch
    {
        "Active" => "text-bg-success",
        "Draft" => "text-bg-warning",
        "Archived" => "text-bg-secondary",
        "Healthy" => "text-bg-success",
        "Degraded" => "text-bg-warning",
        "Idle" => "text-bg-info",
        _ => "text-bg-light",
    };

    public static string TestBadge(string status) => status switch
    {
        "Success" => "text-bg-success",
        "Failed" => "text-bg-danger",
        _ => "text-bg-light border",
    };

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
            if (counter >= suffixes.Length - 1) break;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    public static string FormatRelativeTime(DateTimeOffset? time)
    {
        if (!time.HasValue) return "—";
        var delta = DateTimeOffset.UtcNow - time.Value;
        if (delta.TotalSeconds < 60) return "Vừa xong";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} phút trước";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} giờ trước";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} ngày trước";
        return time.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    }

    /// <summary>A short message for a failed call to the API; the technical detail stays in the logs.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        SharedServicesApiException { StatusCode: 401 } => "Portal chưa có API key hợp lệ để gọi Shared Services API (SharedServices:Api:ApiKey).",
        SharedServicesApiException { StatusCode: 403 } => "API key của portal không đủ quyền cho thao tác này.",
        SharedServicesApiException api => api.Message,
        HttpRequestException or TaskCanceledException => "Không kết nối được Shared Services API. Hãy kiểm tra dịch vụ đang chạy (mặc định http://localhost:5030).",
        _ => "Đã xảy ra lỗi không mong đợi.",
    };
}
