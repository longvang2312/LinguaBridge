using System.ComponentModel.DataAnnotations;

namespace AppInHouse.Identity.Application.Admin.Common;

public class PagingQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, MaxPageSize)]
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>Re-validates the paging values for callers that bypass model validation.</summary>
    public void EnsureValid()
    {
        if (Page < 1)
            throw new BadRequestException("Page must be at least 1.");
        if (PageSize < 1 || PageSize > MaxPageSize)
            throw new BadRequestException($"PageSize must be between 1 and {MaxPageSize}.");
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);
