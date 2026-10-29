namespace LinguaBridge.Application.Abstractions;

/// <summary>Source of the current time, so use cases (SRS due dates, quota windows) stay testable.</summary>
public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}
