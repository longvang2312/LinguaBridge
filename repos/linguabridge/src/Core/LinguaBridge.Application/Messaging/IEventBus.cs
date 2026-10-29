namespace LinguaBridge.Application.Messaging;

/// <summary>In-process publisher for <see cref="IIntegrationEvent"/>s between modules.</summary>
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent;
}
