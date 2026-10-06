using Svm.SharedKernel.Domain;
using Svm.Services.Contracts.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class DomainFoundationTests
{
    [Fact]
    public void IdentifiersRejectEmptyConstructionAndEntitiesRejectDefaultStructIds()
    {
        Assert.Throws<ArgumentException>(() => new StrongId<Tag>(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SampleAggregate(default));
        var value = Guid.NewGuid();
        Assert.NotEqual<object>(new StrongId<Tag>(value), new StrongId<OtherTag>(value));
    }

    [Fact]
    public void EntityIdentityIncludesTheConcreteDomainType()
    {
        var id = new StrongId<Tag>(Guid.NewGuid());
        var first = new SampleAggregate(id);
        var second = new SampleAggregate(id);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual<Entity<StrongId<Tag>>>(first, new OtherEntity(id));
    }

    [Fact]
    public void ValueEqualityUsesTypeAndOrderedComponents()
    {
        var first = new Coordinates(3, 7);
        var equal = new Coordinates(3, 7);
        Assert.Equal(first, equal);
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(first, new Coordinates(7, 3));
        Assert.NotEqual<ValueObject>(first, new DifferentCoordinates(3, 7));
    }

    [Fact]
    public void EventsKeepTheirAssignedIdentityAndCannotBeRemovedByAReader()
    {
        var aggregate = new SampleAggregate(new StrongId<Tag>(Guid.NewGuid()));
        var occurrence = new Happened(Guid.NewGuid(), DateTimeOffset.UtcNow);
        aggregate.Record(occurrence);
        var events = aggregate.DomainEvents;
        Assert.Same(occurrence, Assert.Single(events));
        Assert.Throws<NotSupportedException>(() => ((IList<IDomainEvent>)events).Clear());
        Assert.Throws<InvalidOperationException>(() => aggregate.Record(occurrence));
        Assert.Equal(occurrence.EventId, Assert.Single(aggregate.DomainEvents).EventId);
    }

    [Fact]
    public void EventsRequireExplicitUtcTimeAndAnIdEvenForOtherImplementations()
    {
        Assert.Throws<ArgumentException>(() => new Happened(Guid.Empty, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new Happened(Guid.NewGuid(), DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<ArgumentException>(() => new Happened(Guid.NewGuid(), default));
        var aggregate = new SampleAggregate(new StrongId<Tag>(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => aggregate.Record(new MalformedEvent()));
        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void AcceptedAndCompletedResultsPreserveDifferentMeaningsAndReferences()
    {
        var operation = Guid.NewGuid();
        var work = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var accepted = OperationResult<string>.Accepted(operation, work, "queued", resource);
        var completed = OperationResult<string>.Completed(operation, "recorded", resource);
        Assert.Equal(OperationStatus.Accepted, accepted.Status);
        Assert.Equal(work, accepted.WorkId);
        Assert.Equal(operation, accepted.OperationId);
        Assert.Equal(resource, accepted.ResourceId);
        Assert.Equal(OperationStatus.Completed, completed.Status);
        Assert.Null(completed.WorkId);
        Assert.Throws<ArgumentException>(() => OperationResult<string>.Accepted(operation, Guid.Empty, "invalid"));
    }

    private sealed class Tag;
    private sealed class OtherTag;
    private sealed class SampleAggregate(StrongId<Tag> id) : AggregateRoot<StrongId<Tag>>(id)
    {
        public void Record(IDomainEvent occurrence) => RecordDomainEvent(occurrence);
    }
    private sealed class OtherEntity(StrongId<Tag> id) : Entity<StrongId<Tag>>(id);
    private sealed class Coordinates(int x, int y) : ValueObject
    {
        protected override IEnumerable<object?> GetEqualityComponents() => [x, y];
    }
    private sealed class DifferentCoordinates(int x, int y) : ValueObject
    {
        protected override IEnumerable<object?> GetEqualityComponents() => [x, y];
    }
    private sealed record Happened(Guid Id, DateTimeOffset At) : DomainEvent(Id, At);
    private sealed record MalformedEvent : IDomainEvent
    {
        public Guid EventId => Guid.Empty;
        public DateTimeOffset OccurredAt => default;
    }
}
