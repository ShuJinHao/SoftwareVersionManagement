namespace Svm.Services.Contracts.Framework;

/// <summary>Created only by authenticated host adapters, never by JSON model binding.</summary>
public sealed record CallActor
{
    public CallActor(ActorKind kind, Guid? actorId = null, Guid? softwareId = null,
        Guid? instanceId = null, ModuleOwner? workOwner = null, Guid? workId = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == ActorKind.Anonymous ? actorId is not null : actorId is null || actorId == Guid.Empty)
            throw new ArgumentException("Authenticated actors need an identity; anonymous actors have none.");
        if (softwareId == Guid.Empty || instanceId == Guid.Empty || workId == Guid.Empty)
            throw new ArgumentException("Scope identifiers cannot be empty.");
        if (kind is ActorKind.Instance or ActorKind.EnrollmentGrant or ActorKind.RecoveryGrant && softwareId is null)
            throw new ArgumentException("This actor is bound to a software scope.");
        if (kind is ActorKind.Instance or ActorKind.RecoveryGrant && instanceId is null)
            throw new ArgumentException("This actor is bound to an instance.");
        if (kind == ActorKind.Service && (workOwner is null || !Enum.IsDefined(workOwner.Value)))
            throw new ArgumentException("Internal service identities require an owner.");
        if (kind != ActorKind.Service && (workOwner is not null || workId is not null))
            throw new ArgumentException("Only internal service contexts carry work authority.");
        Kind = kind;
        ActorId = actorId;
        SoftwareId = softwareId;
        InstanceId = instanceId;
        WorkOwner = workOwner;
        WorkId = workId;
    }

    public ActorKind Kind { get; }
    public Guid? ActorId { get; }
    public Guid? SoftwareId { get; }
    public Guid? InstanceId { get; }
    public ModuleOwner? WorkOwner { get; }
    public Guid? WorkId { get; }
}

public sealed record CallContextSnapshot
{
    public CallContextSnapshot(CallActor actor, RequestKind entryKind, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (!Enum.IsDefined(entryKind)) throw new ArgumentOutOfRangeException(nameof(entryKind));
        Actor = actor;
        EntryKind = entryKind;
        CorrelationId = correlationId;
    }

    public CallActor Actor { get; }
    public RequestKind EntryKind { get; }
    public string CorrelationId { get; }
}

public interface ICallContext
{
    CallContextSnapshot? Current { get; }
}

/// <summary>Host-owned scoped adapter over an already authenticated HTTP/work context.</summary>
public interface ITrustedCallContextSource
{
    CallContextSnapshot? GetCurrent();
}
