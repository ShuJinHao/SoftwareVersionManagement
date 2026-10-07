using Svm.SharedKernel.Domain;

namespace Svm.Services.Contracts.Framework;

/// <summary>In-process rules of the event's owning module; no file, network or broker effects.</summary>
public interface IDomainEventHandler<in TEvent> where TEvent : class, IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>Called serially by the root transaction before saving. This is not a message publisher.</summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IAggregateRoot aggregate, IDomainEvent domainEvent, CancellationToken cancellationToken);
}

public sealed class DomainEventOptions
{
    public DomainEventOptions(int maximumEventsPerTransaction = 1000)
    {
        if (maximumEventsPerTransaction <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEventsPerTransaction));
        MaximumEventsPerTransaction = maximumEventsPerTransaction;
    }

    public int MaximumEventsPerTransaction { get; }
}

public enum DomainEventFailure
{
    InvalidOwnership = 1, MissingHandler, InvalidEvent, DuplicateIdentifier, ProcessingLimitExceeded,
    PendingEventsChanged
}

/// <summary>Internal transaction rejection; does not introduce a public HTTP error contract.</summary>
public sealed class DomainEventDispatchException(DomainEventFailure failure) : Exception($"Domain event dispatch failed: {failure}.")
{
    public DomainEventFailure Failure { get; } = failure;
}
