using System.Text.Json;
using BookYourShow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BookYourShow.BuildingBlocks.Messaging;

/// <summary>
/// Maps an event type name (the "type" message attribute) to a typed handler invocation.
/// Populated at startup by <see cref="MessagingServiceCollectionExtensions.AddEventHandler{TEvent,THandler}"/>.
/// </summary>
public sealed class HandlerRegistry
{
    private readonly Dictionary<string, Func<IServiceProvider, string, MessageContext, CancellationToken, Task>> _handlers = new(StringComparer.Ordinal);

    public void Register<TEvent, THandler>()
        where TEvent : IIntegrationEvent
        where THandler : IIntegrationEventHandler<TEvent>
    {
        _handlers[EventTypes.NameOf<TEvent>()] = async (sp, body, ctx, ct) =>
        {
            var evt = JsonSerializer.Deserialize<TEvent>(body, MessagingJson.Options)
                      ?? throw new InvalidOperationException($"Empty body for {ctx.EventType}");
            var handler = sp.GetRequiredService<THandler>();
            await handler.HandleAsync(evt, ctx, ct);
        };
    }

    public bool TryGet(string eventType, out Func<IServiceProvider, string, MessageContext, CancellationToken, Task> invoke)
        => _handlers.TryGetValue(eventType, out invoke!);

    public IReadOnlyCollection<string> RegisteredTypes => _handlers.Keys;
}
