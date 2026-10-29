namespace LinguaBridge.Application.Abstractions;

/// <summary>
/// A write was rejected by a unique index or primary key, usually because a concurrent request got there first.
/// Raised by persistence so use cases can turn it into a conflict without knowing the database provider.
/// </summary>
public sealed class UniqueConstraintViolationException(string? constraintName, Exception innerException)
    : Exception($"Unique constraint '{constraintName}' was violated.", innerException)
{
    public string? ConstraintName { get; } = constraintName;
}
