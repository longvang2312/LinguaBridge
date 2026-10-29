namespace LinguaBridge.Domain.Common;

/// <summary>Something that happened inside an aggregate and that other parts of the same module may react to.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
