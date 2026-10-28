using AppInHouse.Identity.Application.Admin.Common;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

internal static class PagingExtensions
{
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query, PagingQuery paging, CancellationToken cancellationToken)
    {
        paging.EnsureValid();

        var total = await query.LongCountAsync(cancellationToken);
        var skip = (long)(paging.Page - 1) * paging.PageSize;
        if (skip >= total)
            return new PagedResult<T>(Array.Empty<T>(), paging.Page, paging.PageSize, total);

        var items = await query.Skip((int)skip).Take(paging.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, paging.Page, paging.PageSize, total);
    }
}
