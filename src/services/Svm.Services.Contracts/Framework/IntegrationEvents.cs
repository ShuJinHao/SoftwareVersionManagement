namespace Svm.Services.Contracts.Framework;

/// <summary>Internal versioned intent, never a serialized domain event or a public HTTP request.</summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    int SchemaVersion { get; }
    string MessageType { get; }
    DateTimeOffset OccurredAt { get; }
    Guid CorrelationId { get; }
    Guid? CausationId { get; }
    Guid SiteId { get; }
    Guid SoftwareId { get; }
    Guid WorkId { get; }
    long DispatchSequence { get; }
}

/// <summary>Stages one of the fixed internal contracts in the current atomic operation.</summary>
public interface IIntegrationEventOutbox
{
    Task EnqueueAsync<TEvent>(TEvent message, CancellationToken cancellationToken) where TEvent : class, IIntegrationEvent;
}

public enum OutboxFailure { InvalidMessage = 1, InvalidOwnership, TransactionRequired, ConfigurationInvalid }
public sealed class OutboxException(OutboxFailure failure) : Exception($"Integration outbox failed: {failure}.")
{
    public OutboxFailure Failure { get; } = failure;
}
