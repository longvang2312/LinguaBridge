using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.Api.Errors;

/// <summary>
/// Turns the service's own exceptions into RFC 7807 answers: 400 with field errors (and for a request that cannot be read), 404, 409 for a clash with the current state.
/// Anything else is a 500 with no detail (the exception is logged, never sent to the client).
/// </summary>
public sealed class SharedServicesExceptionHandler(IProblemDetailsService problems, ILogger<SharedServicesExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case ValidationException validation:
                problem = new ValidationProblemDetails(validation.Errors.ToDictionary(e => e.Key, e => e.Value))
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more values are not valid.",
                };
                break;
            case BadHttpRequestException badRequest:
                // A body that is not valid JSON, an unknown enum name, a missing required value. In development minimal APIs throw this
                // instead of answering 400 themselves, so without this case it would fall through to the 500 below.
                problem = new ProblemDetails { Status = badRequest.StatusCode, Title = "The request is not valid", Detail = "The request could not be read: check its format and values." };
                break;
            case NotFoundException notFound:
                problem = new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Not found", Detail = notFound.Message };
                break;
            case DomainException domain:
                problem = new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "The request conflicts with the current state", Detail = domain.Message };
                break;
            case DbUpdateConcurrencyException:
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "The configuration was changed by someone else",
                    Detail = "Reload it and try again.",
                };
                break;
            case DbUpdateException when exception.InnerException is not null && IsUniqueViolation(exception.InnerException):
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "The configuration was changed by someone else",
                    Detail = "Another request created the same version first. Reload and try again.",
                };
                break;
            case SecretProtectionException:
                logger.LogError(exception, "A stored secret could not be decrypted.");
                problem = new ProblemDetails { Status = StatusCodes.Status500InternalServerError, Title = "A stored secret cannot be read" };
                break;
            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}.", context.Request.Method, context.Request.Path);
                problem = new ProblemDetails { Status = StatusCodes.Status500InternalServerError, Title = "An unexpected error occurred" };
                break;
        }

        context.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }

    // PostgreSQL SQLSTATE 23505 / SQLite "UNIQUE constraint failed": matched on the text so this layer needs no provider reference.
    private static bool IsUniqueViolation(Exception inner) =>
        inner.Message.Contains("23505", StringComparison.Ordinal)
        || inner.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
        || inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
}
