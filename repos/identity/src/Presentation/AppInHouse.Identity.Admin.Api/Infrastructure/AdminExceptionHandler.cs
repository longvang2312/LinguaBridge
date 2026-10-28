using Microsoft.AspNetCore.Diagnostics;

namespace AppInHouse.Identity.Admin.Api.Infrastructure;

/// <summary>
/// Last line of defence for failures outside the MVC pipeline and for unexpected exceptions: a generic 500 that reveals
/// nothing about the internals (the exception is only logged by the exception-handler middleware).
/// </summary>
public sealed class AdminExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499; // client closed the request
            return true;
        }

        var problem = AdminProblems.TryCreate(exception) ?? AdminProblems.Unexpected();
        problem.Instance = httpContext.Request.Path;

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }
}
