using Svm.Services.Contracts.Messaging.V1;

namespace Svm.Services.Contracts.Framework;

public interface IIntegrationEventHandler<in TEvent> where TEvent : class, IIntegrationEvent
{
    Task<OperationResultReference> HandleAsync(TEvent message, IntegrationConsumptionContext context, CancellationToken cancellationToken);
}

/// <summary>Authoritative current work and service identity, obtained by a trusted host/owner adapter.</summary>
public sealed record IntegrationWorkAuthority(CallActor Actor, ModuleOwner Owner, Guid SiteId, Guid SoftwareId,
    Guid WorkId, int WorkKind, long DispatchSequence, Guid DispatchEventId,
    long MessageDispatchSequence, Guid MessageDispatchEventId);

public interface IIntegrationWorkAuthorizer
{
    ValueTask<IntegrationWorkAuthority> AuthorizeAsync(IIntegrationEvent message, ModuleOwner owner,
        bool insideTransaction, CancellationToken cancellationToken);
}

public sealed record IntegrationConsumptionContext(Guid MessageId, ModuleOwner Owner, CallActor Actor,
    Guid SoftwareId, Guid WorkId, bool IsObsolete);

public interface IIntegrationEventPreflight
{
    Task VerifyAsync(IIntegrationEvent message, CancellationToken cancellationToken);
}

public interface IIntegrationEventDispatcher<in TEvent> where TEvent : class, IIntegrationEvent
{
    Task<OperationResultReference> DispatchAsync(TEvent message, CancellationToken cancellationToken);
}

/// <summary>Readonly infrastructure capability. It exposes no transaction, connection or save operation.</summary>
public interface IIntegrationConsumptionTransaction { void EnsureActive(); }

/// <summary>Reauthorizes and reads committed facts in a fresh scope, without executing a handler.</summary>
public interface IIntegrationConsumptionRecovery
{
    Task<OperationResultReference?> FindAsync(IIntegrationEvent message, CancellationToken cancellationToken);
}

/// <summary>Explicit immutable registration, never message-provided reflection/type names.</summary>
public sealed class IntegrationConsumerBinding(Type eventType, Type handlerType)
{
    public Type EventType { get; } = eventType ?? throw new ArgumentNullException(nameof(eventType));
    public Type HandlerType { get; } = handlerType ?? throw new ArgumentNullException(nameof(handlerType));
    public static IntegrationConsumerBinding Single<TEvent, THandler>()
        where TEvent : class, IIntegrationEvent where THandler : class, IIntegrationEventHandler<TEvent> => new(typeof(TEvent), typeof(THandler));
}

public sealed record IntegrationEventDescriptor(Type EventType, ModuleOwner Owner, string Queue)
{
    public static IntegrationEventDescriptor For(Type type) => type == typeof(PackageWorkAvailableV1)
        ? new(type, ModuleOwner.Packages, "svm.package-work.available.v1") : type == typeof(TaskPreparationAvailableV1)
        ? new(type, ModuleOwner.Tasks, "svm.task-preparation.available.v1") : type == typeof(TaskControlAvailableV1)
        ? new(type, ModuleOwner.Tasks, "svm.task-control.available.v1") : throw new IntegrationConsumptionException(ConsumptionFailure.InvalidMessage);
    public static int WorkKind(IIntegrationEvent message) => message switch
    {
        PackageWorkAvailableV1 p when Enum.IsDefined(p.WorkKind) => (int)p.WorkKind,
        TaskPreparationAvailableV1 p when Enum.IsDefined(p.WorkKind) => (int)p.WorkKind,
        TaskControlAvailableV1 p when Enum.IsDefined(p.WorkKind) => (int)p.WorkKind,
        _ => throw new IntegrationConsumptionException(ConsumptionFailure.InvalidMessage)
    };
}

public enum ConsumptionFailure { InvalidMessage = 1, InvalidOwnership, PermissionDenied, ConfigurationInvalid, Conflict, TransientClaimConflict, TransactionRequired }
public sealed class IntegrationConsumptionException(ConsumptionFailure failure) : Exception($"Integration consumption failed: {failure}.")
{
    public ConsumptionFailure Failure { get; } = failure;
}
