using AppInHouse.Identity.Application.Admin.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Infrastructure;

/// <summary>Base of every portal page: requires a signed-in user and offers the toast helpers.</summary>
[Authorize]
public abstract class PortalController : Controller
{
    protected void Success(string message) => TempData["Success"] = message;

    protected void Failure(string message) => TempData["Error"] = message;

    /// <summary>The validation messages of the current request, for a flash message when the form cannot be re-displayed.</summary>
    protected string ModelErrors()
        => string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).Where(m => m.Length > 0).Distinct());

    /// <summary>Reads every page of a paged use case (portal lists such as roles or permissions are small).</summary>
    protected static async Task<List<T>> AllAsync<T>(Func<int, Task<PagedResult<T>>> page)
    {
        var all = new List<T>();
        for (var number = 1; ; number++)
        {
            var result = await page(number);
            all.AddRange(result.Items);
            if (all.Count >= result.TotalCount || result.Items.Count == 0)
                return all;
        }
    }

    protected static List<string> Lines(string? text)
        => (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
