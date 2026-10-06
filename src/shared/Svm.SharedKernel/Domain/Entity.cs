namespace Svm.SharedKernel.Domain;

public abstract class Entity<TId> : IEquatable<Entity<TId>> where TId : notnull, IStrongId
{
    protected Entity(TId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        // Struct identifiers can be default-initialized without invoking their constructor.
        if (id.Value == Guid.Empty) throw new ArgumentException("An entity needs an assigned identifier.", nameof(id));
        Id = id;
    }

    public TId Id { get; }

    public bool Equals(Entity<TId>? other) => other is not null &&
        GetType() == other.GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public sealed override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);
    public sealed override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
