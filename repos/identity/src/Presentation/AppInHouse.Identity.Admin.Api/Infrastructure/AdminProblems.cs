using AppInHouse.Identity.Application.Admin.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Admin.Api.Infrastructure;

/// <summary>Maps the expected admin failures onto RFC 7807 problem details.</summary>
public static class AdminProblems
{
    /// <summary>Null when the exception is not an expected, client-facing failure.</summary>
    public static ProblemDetails? TryCreate(Exception exception) => exception switch
    {
        NotFoundException => Create(StatusCodes.Status404NotFound, "Not found", exception.Message),
        ForbiddenException => Create(StatusCodes.Status403Forbidden, "Forbidden", exception.Message),
        ConflictException => Create(StatusCodes.Status409Conflict, "Conflict", exception.Message),
        BadRequestException { Errors: { } errors } bad => new ValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad request",
            Detail = bad.Message,
        },
        BadRequestException => Create(StatusCodes.Status400BadRequest, "Bad request", exception.Message),
        // Argument errors raised by the domain guards describe invalid input (their messages are our own).
        ArgumentException => Create(StatusCodes.Status400BadRequest, "Bad request", exception.Message),
        DbUpdateConcurrencyException => Create(StatusCodes.Status409Conflict, "Conflict", "The data was modified by someone else. Reload and try again."),
        _ => null,
    };

    public static ProblemDetails Unexpected() => Create(StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred.");

    private static ProblemDetails Create(int status, string title, string detail)
        => new() { Status = status, Title = title, Detail = detail };
}

/// <summary>
/// Handles the expected failures inside the MVC pipeline so they never surface as "unhandled exception" errors in the
/// logs; a 403 is logged as a warning because it may indicate probing.
/// </summary>
public sealed class AdminExceptionFilter : IExceptionFilter
{
    private readonly ILogger<AdminExceptionFilter> _logger;

    public AdminExceptionFilter(ILogger<AdminExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        var problem = AdminProblems.TryCreate(context.Exception);
        if (problem is null)
            return;

        if (problem.Status == StatusCodes.Status403Forbidden)
        {
            _logger.LogWarning(
                "Forbidden {Method} {Path}: {Reason}",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path, context.Exception.Message);
        }

        problem.Instance = context.HttpContext.Request.Path;
        context.Result = new ObjectResult(problem) { StatusCode = problem.Status };
        context.ExceptionHandled = true;
    }
}
