namespace AppInHouse.SharedServices.Application.Common;

/// <summary>The request is not acceptable; <see cref="Errors"/> maps a field to what is wrong with it.</summary>
public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("One or more values are not valid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class NotFoundException(string message) : Exception(message);
