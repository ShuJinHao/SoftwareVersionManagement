namespace Svm.SharedKernel.Domain;

public interface IStrongId
{
    Guid Value { get; }
}

/// <summary>The tag keeps identifiers from different domains distinct at compile time.</summary>
public readonly record struct StrongId<TTag> : IStrongId
{
    public StrongId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("An identifier cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("D");
}
