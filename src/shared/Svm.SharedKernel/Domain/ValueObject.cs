namespace Svm.SharedKernel.Domain;

/// <summary>Derived types must expose immutable equality components.</summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other) => other is not null && GetType() == other.GetType() &&
        GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());

    public sealed override bool Equals(object? obj) => obj is ValueObject other && Equals(other);

    public sealed override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GetType());
        foreach (var component in GetEqualityComponents()) hash.Add(component);
        return hash.ToHashCode();
    }
}
