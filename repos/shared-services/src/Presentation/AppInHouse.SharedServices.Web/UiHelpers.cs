using AppInHouse.SharedServices.ClientSdk;

namespace AppInHouse.SharedServices.Web;

public static class UiHelpers
{
    public static string StatusBadge(string status) => status switch
    {
        "Active" => "text-bg-success",
        "Draft" => "text-bg-warning",
        "Archived" => "text-bg-secondary",
        _ => "text-bg-light",
    };

    public static string TestBadge(string status) => status switch
    {
        "Success" => "text-bg-success",
        "Failed" => "text-bg-danger",
        _ => "text-bg-light border",
    };

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
