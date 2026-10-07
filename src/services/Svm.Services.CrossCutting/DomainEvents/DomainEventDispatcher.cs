using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.Services.CrossCutting.DomainEvents;

internal interface IDomainEventSubscription
{
    Type EventType { get; }
    Type HandlerType { get; }
    Task HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

internal sealed class DomainEventSubscription<TEvent, THandler>(THandler handler) : IDomainEventSubscription
    where TEvent : class, IDomainEvent where THandler : class, IDomainEventHandler<TEvent>
{
    public Type EventType => typeof(TEvent);
    public Type HandlerType => typeof(THandler);
    public Task HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken) =>
        handler.HandleAsync((TEvent)domainEvent, cancellationToken);
}

internal sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly DomainEventCatalog _catalog;
    private readonly IReadOnlyDictionary<(Type Event, Type Handler), IDomainEventSubscription> _subscriptions;

    public DomainEventDispatcher(DomainEventCatalog catalog, IEnumerable<IDomainEventSubscription> subscriptions)
    {
        _catalog = catalog;
        _subscriptions = subscriptions.ToDictionary(s => (s.EventType, s.HandlerType));
        var expected = catalog.Bindings.SelectMany(b => b.Handlers.Select(h => (b.EventType, h.HandlerType))).ToHashSet();
        if (!expected.SetEquals(_subscriptions.Keys))
            throw new InvalidOperationException("Domain event subscriptions must match the immutable catalog.");
    }

    public async Task DispatchAsync(IAggregateRoot aggregate, IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();
        if (domainEvent.EventId == Guid.Empty || domainEvent.OccurredAt == default || domainEvent.OccurredAt.Offset != TimeSpan.Zero)
            throw new DomainEventDispatchException(DomainEventFailure.InvalidEvent);
        var binding = _catalog.Get(aggregate, domainEvent);
        foreach (var subscriber in binding.Handlers.OrderBy(h => h.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _subscriptions[(binding.EventType, subscriber.HandlerType)].HandleAsync(domainEvent, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
