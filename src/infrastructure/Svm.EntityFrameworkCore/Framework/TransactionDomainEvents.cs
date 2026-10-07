using Microsoft.EntityFrameworkCore;
using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Framework;

/// <summary>One root transaction's processed objects, retained until a confirmed database commit.</summary>
internal sealed class TransactionDomainEvents(SvmDbContext context, IDomainEventDispatcher dispatcher, DomainEventOptions options)
{
    private sealed record Pending(IAggregateRoot Aggregate, IDomainEvent Event, Guid Id, DateTimeOffset OccurredAt);
    private readonly Dictionary<Guid, Pending> _seen = [];
    private readonly Dictionary<IAggregateRoot, List<IDomainEvent>> _processed = new(ReferenceEqualityComparer.Instance);

    internal async Task DispatchAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateStableEvents();
            var pending = CollectPending();
            if (pending.Count == 0) break;
            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dispatcher.DispatchAsync(item.Aggregate, item.Event, cancellationToken);
                if (!_processed.TryGetValue(item.Aggregate, out var events))
                    _processed.Add(item.Aggregate, events = []);
                events.Add(item.Event);
            }
        }
        ValidateBeforeSave();
    }

    internal void ValidateBeforeSave()
    {
        ValidateStableEvents();
        var tracked = context.ChangeTracker.Entries().Select(e => e.Entity).OfType<IAggregateRoot>()
            .ToHashSet(ReferenceEqualityComparer.Instance);
        if (_processed.Keys.Any(aggregate => !tracked.Contains(aggregate)))
            throw new DomainEventDispatchException(DomainEventFailure.PendingEventsChanged);
        ValidateNoNewEvents(tracked.OfType<IAggregateRoot>());
    }

    internal void ValidateAfterSave()
    {
        ValidateStableEvents();
        // Deleted aggregates may now be detached. Their pending facts still belong to this batch.
        ValidateNoNewEvents(context.ChangeTracker.Entries().Select(e => e.Entity).OfType<IAggregateRoot>().Concat(_processed.Keys));
    }

    internal void AcknowledgeCommitted()
    {
        foreach (var (aggregate, events) in _processed)
            ((ITransactionalDomainEvents)aggregate).AcknowledgeDomainEvents(events);
    }

    private List<Pending> CollectPending()
    {
        var pending = new List<Pending>();
        foreach (var aggregate in context.ChangeTracker.Entries().Select(e => e.Entity).OfType<IAggregateRoot>())
        foreach (var domainEvent in aggregate.DomainEvents.ToArray())
        {
            if (aggregate is not ITransactionalDomainEvents || domainEvent is null || domainEvent.EventId == Guid.Empty ||
                domainEvent.OccurredAt == default || domainEvent.OccurredAt.Offset != TimeSpan.Zero)
                throw new DomainEventDispatchException(DomainEventFailure.InvalidEvent);
            if (_seen.TryGetValue(domainEvent.EventId, out var previous))
            {
                if (!ReferenceEquals(previous.Aggregate, aggregate) || !ReferenceEquals(previous.Event, domainEvent))
                    throw new DomainEventDispatchException(DomainEventFailure.DuplicateIdentifier);
                continue;
            }
            if (_seen.Count >= options.MaximumEventsPerTransaction)
                throw new DomainEventDispatchException(DomainEventFailure.ProcessingLimitExceeded);
            var item = new Pending(aggregate, domainEvent, domainEvent.EventId, domainEvent.OccurredAt);
            _seen.Add(item.Id, item);
            pending.Add(item);
        }
        return pending;
    }

    private void ValidateStableEvents()
    {
        if (_seen.Values.Any(item => item.Event.EventId != item.Id || item.Event.OccurredAt != item.OccurredAt))
            throw new DomainEventDispatchException(DomainEventFailure.InvalidEvent);
    }

    private void ValidateNoNewEvents(IEnumerable<IAggregateRoot> aggregates)
    {
        foreach (var aggregate in aggregates)
        foreach (var domainEvent in aggregate.DomainEvents)
            if (!_seen.TryGetValue(domainEvent.EventId, out var item) || !ReferenceEquals(item.Aggregate, aggregate) ||
                !ReferenceEquals(item.Event, domainEvent) || !_processed.TryGetValue(aggregate, out var events) ||
                !events.Any(processed => ReferenceEquals(processed, domainEvent)))
                throw new DomainEventDispatchException(DomainEventFailure.PendingEventsChanged);
    }
}
