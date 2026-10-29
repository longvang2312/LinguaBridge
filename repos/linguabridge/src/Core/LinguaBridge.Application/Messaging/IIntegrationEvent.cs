namespace LinguaBridge.Application.Messaging;

/// <summary>
/// A fact published by one module for other modules to react to. This is the only way modules talk to each
/// other: they never reference one another's assemblies, only the event contracts declared here.
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}
