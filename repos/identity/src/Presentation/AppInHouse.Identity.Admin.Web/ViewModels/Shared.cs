using AppInHouse.Identity.Application.Admin.Common;

namespace AppInHouse.Identity.Admin.Web.ViewModels;

public sealed record PagerModel(int Page, int PageSize, long TotalCount)
{
    public int TotalPages => (int)Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    public static PagerModel From<T>(PagedResult<T> result) => new(result.Page, result.PageSize, result.TotalCount);
}

public sealed class ErrorViewModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
