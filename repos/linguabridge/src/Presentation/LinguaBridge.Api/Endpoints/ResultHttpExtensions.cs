using LinguaBridge.Application.Abstractions;

namespace LinguaBridge.Api.Endpoints;

/// <summary>Maps use-case failures to RFC 9457 problem responses; the stable <c>code</c> lets clients branch without parsing text.</summary>
internal static class ResultHttpExtensions
{
    public static IResult ToProblem(this Error error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        return error.Type switch
        {
            ErrorType.Validation => Results.ValidationProblem(
                error.Details?.ToDictionary(d => d.Key, d => d.Value) ?? [],
                title: error.Message,
                extensions: extensions),
            ErrorType.NotFound => Results.Problem(detail: error.Message, statusCode: StatusCodes.Status404NotFound, title: "Not found", extensions: extensions),
            ErrorType.Conflict => Results.Problem(detail: error.Message, statusCode: StatusCodes.Status409Conflict, title: "Conflict", extensions: extensions),
            _ => throw new ArgumentOutOfRangeException(nameof(error), error.Type, "Unknown error type."),
        };
    }

    public static IResult ToNoContent(this Result result) => result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();

    public static IResult ToOk<T>(this Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem();

    public static IResult ToCreated(this Result<Guid> result, Func<Guid, string> location) =>
        result.IsSuccess ? Results.Created(location(result.Value), new IdResponse(result.Value)) : result.Error!.ToProblem();
}

public sealed record IdResponse(Guid Id);
