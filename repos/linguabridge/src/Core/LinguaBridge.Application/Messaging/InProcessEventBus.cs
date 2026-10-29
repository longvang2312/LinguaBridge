using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.Application.Messaging;

/// <summary>
/// Delivers an event to every registered handler in the caller's scope, one after another.
/// A failing handler stops delivery and surfaces to the publisher; durable delivery (outbox) comes later.
/// </summary>
internal sealed class InProcessEventBus(IServiceProvider serviceProvider) : IEventBus
{
    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        foreach (var handler in serviceProvider.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await handler.HandleAsync(integrationEvent, cancellationToken);
        }
    }
}
