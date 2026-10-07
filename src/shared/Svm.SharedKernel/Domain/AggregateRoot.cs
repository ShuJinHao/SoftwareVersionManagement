using System.Collections.ObjectModel;

namespace Svm.SharedKernel.Domain;

public interface IAggregateRoot
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
}

internal interface ITransactionalDomainEvents
{
    void AcknowledgeDomainEvents(IReadOnlyList<IDomainEvent> processed);
}

public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot, ITransactionalDomainEvents where TId : notnull, IStrongId
{
    private readonly List<IDomainEvent> _events = [];
    private readonly ReadOnlyCollection<IDomainEvent> _view;

    protected AggregateRoot(TId id) : base(id) => _view = _events.AsReadOnly();

    public IReadOnlyList<IDomainEvent> DomainEvents => _view;

    protected void RecordDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.EventId == Guid.Empty || domainEvent.OccurredAt == default || domainEvent.OccurredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Domain events require a stable identifier and UTC time.", nameof(domainEvent));
        if (_events.Any(e => e.EventId == domainEvent.EventId))
            throw new InvalidOperationException("An event identifier is already recorded on this aggregate.");
        _events.Add(domainEvent);
    }

    void ITransactionalDomainEvents.AcknowledgeDomainEvents(IReadOnlyList<IDomainEvent> processed)
    {
        // Match the actual dispatched objects, never discard unrelated pending events.
        foreach (var domainEvent in processed)
        {
            var index = _events.FindIndex(pending => ReferenceEquals(pending, domainEvent));
            if (index >= 0) _events.RemoveAt(index);
        }
    }
}
