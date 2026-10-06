namespace Svm.SharedKernel.Domain;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    protected DomainEvent(Guid eventId, DateTimeOffset occurredAt)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("An event needs a stable identifier.", nameof(eventId));
        if (occurredAt == default || occurredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("An event needs an explicit UTC occurrence time.", nameof(occurredAt));
        EventId = eventId;
        OccurredAt = occurredAt;
    }

    public Guid EventId { get; }
    public DateTimeOffset OccurredAt { get; }
}
