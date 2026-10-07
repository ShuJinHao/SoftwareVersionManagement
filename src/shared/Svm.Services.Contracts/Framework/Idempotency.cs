namespace Svm.Services.Contracts.Framework;

/// <summary>Explicitly projected semantic values, never a serialized request or database entity.</summary>
public sealed class OperationValue
{
    internal enum ValueKind : byte { Null, Text, Integer, Number, Boolean, Identifier, Timestamp, Object, Array }
    private OperationValue(ValueKind kind, object? value) { Kind = kind; Value = value; }
    internal ValueKind Kind { get; }
    internal object? Value { get; }

    public static OperationValue Null { get; } = new(ValueKind.Null, null);
    public static OperationValue Text(string value) => new(ValueKind.Text, value ?? throw new ArgumentNullException(nameof(value)));
    public static OperationValue Integer(long value) => new(ValueKind.Integer, value);
    public static OperationValue Number(decimal value) => new(ValueKind.Number, value);
    public static OperationValue Boolean(bool value) => new(ValueKind.Boolean, value);
    public static OperationValue Identifier(Guid value) => new(ValueKind.Identifier, value);
    public static OperationValue Timestamp(DateTimeOffset value) => new(ValueKind.Timestamp, value.ToUniversalTime());
    public static OperationValue Object(params OperationField[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var copy = fields.ToArray();
        if (copy.Any(f => f is null) || copy.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Semantic fields must have unique names.", nameof(fields));
        return new(ValueKind.Object, System.Array.AsReadOnly(copy));
    }
    public static OperationValue Array(params OperationValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = values.ToArray();
        if (copy.Any(v => v is null)) throw new ArgumentException("Use an explicit null value.", nameof(values));
        return new(ValueKind.Array, System.Array.AsReadOnly(copy));
    }
    public override string ToString() => "Operation semantic value (redacted)";
}

public sealed class OperationField
{
    public OperationField(string name, OperationValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
    public string Name { get; }
    public OperationValue Value { get; }
    public override string ToString() => "Operation semantic field (redacted)";
}

/// <summary>The key comes from the protocol; the adapter explicitly projects route and parsed body fields.</summary>
public sealed class OperationRequestData
{
    public OperationRequestData(Guid key, OperationValue route, OperationValue body)
    {
        if (key == Guid.Empty) throw new RequestRejectedException(RequestFailure.InvalidRequest);
        if (route?.Kind != OperationValue.ValueKind.Object || body?.Kind != OperationValue.ValueKind.Object)
            throw new ArgumentException("Route and body must be semantic objects.");
        Key = key; Route = route; Body = body;
    }
    public Guid Key { get; }
    internal OperationValue Route { get; }
    internal OperationValue Body { get; }
    public override string ToString() => "Operation request data (redacted)";
}

/// <summary>No response body or credentials are retained. Accepted means a work item was accepted.</summary>
public sealed record OperationResultReference
{
    public OperationResultReference(Guid operationId, OperationStatus status, Guid? resourceId = null, Guid? workId = null)
    {
        if (operationId == Guid.Empty || resourceId == Guid.Empty || workId == Guid.Empty || !Enum.IsDefined(status) ||
            (status == OperationStatus.Accepted ? workId is null : workId is not null))
            throw new ArgumentException("Invalid operation result reference.");
        OperationId = operationId; Status = status; ResourceId = resourceId; WorkId = workId;
    }
    public Guid OperationId { get; }
    public OperationStatus Status { get; }
    public Guid? ResourceId { get; }
    public Guid? WorkId { get; }
}

public sealed record StoredOperationResult(string RequestDigest, OperationResultReference Result);

/// <summary>Read-only capability created by the framework from fixed metadata and a trusted call context.</summary>
public sealed class OperationIdentity
{
    internal OperationIdentity(ModuleOwner owner, CallActor actor, string operation, Guid key, string requestDigest)
    {
        if (!Enum.IsDefined(owner) || actor.Kind == ActorKind.Anonymous || actor.ActorId is null ||
            string.IsNullOrWhiteSpace(operation) || operation.Length > 128 || key == Guid.Empty ||
            requestDigest.Length != 64 || requestDigest.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        Owner = owner; ActorKind = actor.Kind; SubjectId = actor.ActorId.Value;
        Operation = operation; Key = key; RequestDigest = requestDigest;
    }
    public ModuleOwner Owner { get; }
    public ActorKind ActorKind { get; }
    public Guid SubjectId { get; }
    public string Operation { get; }
    public Guid Key { get; }
    public string RequestDigest { get; }
    public override string ToString() => "Bound operation identity (redacted)";
}

public interface IOperationContext { OperationIdentity? Current { get; } }

/// <summary>All methods are bound to the current operation; none accepts an arbitrary owner or subject.</summary>
public interface IOperationResultStore
{
    Task<StoredOperationResult?> FindAsync(CancellationToken cancellationToken);
    Task<bool> TryAcquireAsync(Guid operationId, CancellationToken cancellationToken);
    Task CompleteAsync(OperationResultReference result, CancellationToken cancellationToken);
}

/// <summary>Explicit request projection and safe reconstruction from a result reference; no generic response cache.</summary>
public interface IIdempotencyRequestAdapter<in TRequest, TResponse> where TRequest : notnull
{
    OperationRequestData Describe(TRequest request);
    OperationResultReference GetReference(TResponse response);
    Task<TResponse> RestoreAsync(OperationResultReference reference, CancellationToken cancellationToken);
}
