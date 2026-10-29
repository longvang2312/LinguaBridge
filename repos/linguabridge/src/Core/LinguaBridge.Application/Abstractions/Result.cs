namespace LinguaBridge.Application.Abstractions;

public enum ErrorType
{
    /// <summary>The request is malformed or breaks a field rule (HTTP 400).</summary>
    Validation,

    /// <summary>The thing asked for does not exist, or is not visible to the caller (HTTP 404).</summary>
    NotFound,

    /// <summary>The request is well-formed but clashes with the current state, e.g. a duplicate or a forbidden transition (HTTP 409).</summary>
    Conflict,
}

/// <param name="Code">Stable machine-readable code, e.g. <c>course.not_found</c>.</param>
/// <param name="Details">For <see cref="ErrorType.Validation"/>: messages grouped by field name.</param>
public sealed record Error(ErrorType Type, string Code, string Message, IReadOnlyDictionary<string, string[]>? Details = null)
{
    public static Error Validation(IReadOnlyDictionary<string, string[]> details) =>
        new(ErrorType.Validation, "validation.failed", "One or more validation errors occurred.", details);

    public static Error Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static Error NotFound(string code, string message) => new(ErrorType.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorType.Conflict, code, message);
}

/// <summary>Outcome of a use case. Expected failures are values, not exceptions.</summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value)
        : base(null) => _value = value;

    private Result(Error error)
        : base(error ?? throw new ArgumentNullException(nameof(error)))
    {
    }

    /// <summary>The value of a successful result; throws if the result is a failure.</summary>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"The result is a failure ({Error!.Code}) and has no value.");

    public static Result<T> Success(T value) => new(value);

    public static new Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
