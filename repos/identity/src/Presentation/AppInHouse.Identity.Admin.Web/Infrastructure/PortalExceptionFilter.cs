using AppInHouse.Identity.Application.Admin.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace AppInHouse.Identity.Admin.Web.Infrastructure;

/// <summary>
/// Turns the expected failures of the admin use cases into friendly pages: 403 and 404 pages, and for a refused form
/// (conflict, invalid request) a message on the page the user came from. Anything else is left to the error handler.
/// </summary>
public sealed class PortalExceptionFilter : IExceptionFilter
{
    private readonly IModelMetadataProvider _metadata;
    private readonly ITempDataDictionaryFactory _tempData;

    public PortalExceptionFilter(IModelMetadataProvider metadata, ITempDataDictionaryFactory tempData)
    {
        _metadata = metadata;
        _tempData = tempData;
    }

    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case ForbiddenException:
                context.Result = Page(context, "Forbidden", StatusCodes.Status403Forbidden, "You are not allowed to do this.");
                break;
            case NotFoundException e:
                context.Result = Page(context, "NotFound", StatusCodes.Status404NotFound, e.Message);
                break;
            case ConflictException e:
                context.Result = Refused(context, e.Message, StatusCodes.Status409Conflict);
                break;
            case BadRequestException e:
                context.Result = Refused(context, e.Message, StatusCodes.Status400BadRequest);
                break;
            default:
                return;
        }

        context.ExceptionHandled = true;
    }

    private ViewResult Page(ExceptionContext context, string view, int status, string message)
        => new()
        {
            ViewName = view,
            StatusCode = status,
            ViewData = new ViewDataDictionary<string>(_metadata, context.ModelState) { Model = message },
            TempData = _tempData.GetTempData(context.HttpContext),
        };

    /// <summary>A refused form goes back to the page it was submitted from (same site only) with the reason.</summary>
    private IActionResult Refused(ExceptionContext context, string message, int status)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsPost(request.Method) && LocalReferer(request) is { } back)
        {
            _tempData.GetTempData(context.HttpContext)["Error"] = message;
            return new LocalRedirectResult(back);
        }

        return Page(context, "Problem", status, message);
    }

    private static string? LocalReferer(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var referer))
            return null;
        if (!string.Equals(referer.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return null;

        var local = referer.PathAndQuery;
        return local.StartsWith('/') && !local.StartsWith("//", StringComparison.Ordinal) && !local.StartsWith("/\\", StringComparison.Ordinal)
            ? local
            : null;
    }
}
