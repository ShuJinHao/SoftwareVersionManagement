using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Idempotency;

namespace Svm.Services.CrossCutting.Consumption;

internal sealed class IntegrationConsumptionPreflight(IIntegrationWorkAuthorizer authorizer,
    IOperationContext operations, IOperationResultStore results) : IIntegrationEventPreflight
{
    internal IIntegrationEvent? Message { get; private set; }
    internal IntegrationConsumptionContext? Context { get; private set; }
    public async Task VerifyAsync(IIntegrationEvent message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        if (Message is not null) throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        var descriptor = IntegrationEventDescriptor.For(message.GetType());
        if (message.EventId == Guid.Empty || message.SchemaVersion != 1 || message.MessageType != descriptor.Queue ||
            message.OccurredAt == default || message.OccurredAt.Offset != TimeSpan.Zero || message.CorrelationId == Guid.Empty ||
            message.CausationId == Guid.Empty || message.SiteId == Guid.Empty || message.SoftwareId == Guid.Empty ||
            message.WorkId == Guid.Empty || message.DispatchSequence < 1)
            throw new IntegrationConsumptionException(ConsumptionFailure.InvalidMessage);
        var authority = await authorizer.AuthorizeAsync(message, descriptor.Owner, false, cancellationToken);
        Context = Check(message, descriptor, authority);
        if (operations is not ScopedOperationContext operation) throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        operation.Bind(new OperationIdentity(descriptor.Owner, authority.Actor, "integration.receive." + descriptor.Queue,
            message.EventId, Digest(message)));
        if (await results.FindAsync(cancellationToken) is { } stored && stored.RequestDigest != Digest(message))
            throw new IntegrationConsumptionException(ConsumptionFailure.Conflict);
        Message = message;
    }
    internal static IntegrationConsumptionContext Check(IIntegrationEvent message, IntegrationEventDescriptor descriptor, IntegrationWorkAuthority a)
    {
        if (a.Actor.Kind != ActorKind.Service || a.Owner != descriptor.Owner || a.Actor.WorkOwner != descriptor.Owner ||
            a.SiteId != message.SiteId || a.SoftwareId != message.SoftwareId || a.WorkId != message.WorkId ||
            a.Actor.SoftwareId != a.SoftwareId || a.Actor.WorkId != a.WorkId || a.WorkKind != IntegrationEventDescriptor.WorkKind(message))
            throw new IntegrationConsumptionException(ConsumptionFailure.InvalidOwnership);
        if (a.MessageDispatchEventId != message.EventId || a.MessageDispatchSequence != message.DispatchSequence ||
            a.DispatchEventId == Guid.Empty || a.DispatchSequence < message.DispatchSequence ||
            a.DispatchSequence == message.DispatchSequence && a.DispatchEventId != message.EventId)
            throw new IntegrationConsumptionException(ConsumptionFailure.Conflict);
        return new(message.EventId, descriptor.Owner, a.Actor, a.SoftwareId, a.WorkId, message.DispatchSequence < a.DispatchSequence);
    }
    internal static string Digest(IIntegrationEvent m)
    {
        var data = new OperationRequestData(m.EventId, OperationValue.Object(), OperationValue.Object(
            new("eventId", OperationValue.Identifier(m.EventId)), new("schemaVersion", OperationValue.Integer(m.SchemaVersion)),
            new("messageType", OperationValue.Text(m.MessageType)), new("occurredAt", OperationValue.Timestamp(m.OccurredAt)),
            new("correlationId", OperationValue.Identifier(m.CorrelationId)), new("causationId", m.CausationId is { } id ? OperationValue.Identifier(id) : OperationValue.Null),
            new("siteId", OperationValue.Identifier(m.SiteId)), new("softwareId", OperationValue.Identifier(m.SoftwareId)),
            new("workId", OperationValue.Identifier(m.WorkId)), new("workKind", OperationValue.Integer(IntegrationEventDescriptor.WorkKind(m))),
            new("dispatchSequence", OperationValue.Integer(m.DispatchSequence))));
        var policy = new RequestPolicy(new RequestPolicyAttribute("integration.receive." + m.MessageType, IntegrationEventDescriptor.For(m.GetType()).Owner,
            RequestKind.Internal, RequestScope.InternalWork, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult,
            ValidationMode.Required, ActorKind.Service) { Permission = "integration.receive" });
        return OperationDigest.Create(policy, AuthorizationTarget.Work(IntegrationEventDescriptor.For(m.GetType()).Owner, m.WorkId, m.SoftwareId), data);
    }
}

internal sealed class IntegrationConsumptionDispatcher<TEvent, THandler>(IIntegrationEventPreflight verification,
    IIntegrationWorkAuthorizer authorizer, IIntegrationConsumptionTransaction transaction, IUnitOfWork unit, IOperationResultStore results, THandler handler)
    : IIntegrationEventDispatcher<TEvent> where TEvent : class, IIntegrationEvent where THandler : class, IIntegrationEventHandler<TEvent>
{
    public Task<OperationResultReference> DispatchAsync(TEvent message, CancellationToken cancellationToken)
    {
        transaction.EnsureActive();
        return unit.ExecuteAsync(message.EventId, async token =>
        {
            var preflight = verification as IntegrationConsumptionPreflight ?? throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
            if (!ReferenceEquals(message, preflight.Message) || preflight.Context is not { } expected)
                throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
            var current = IntegrationConsumptionPreflight.Check(message, IntegrationEventDescriptor.For(typeof(TEvent)),
                await authorizer.AuthorizeAsync(message, expected.Owner, true, token));
            if (current.Actor != expected.Actor) throw new IntegrationConsumptionException(ConsumptionFailure.PermissionDenied);
            async Task<OperationResultReference?> Find()
            {
                var stored = await results.FindAsync(token);
                if (stored is null) return null;
                if (stored.RequestDigest != IntegrationConsumptionPreflight.Digest(message))
                    throw new IntegrationConsumptionException(ConsumptionFailure.Conflict);
                return stored.Result;
            }
            if (await Find() is { } retained) return retained;
            if (!await results.TryAcquireAsync(message.EventId, token))
                return await Find() ?? throw new PersistenceException(PersistenceFailure.DependencyUnavailable, message.EventId);
            var result = current.IsObsolete ? new(message.EventId, OperationStatus.Completed, message.WorkId)
                : await handler.HandleAsync(message, current, token);
            if (result.OperationId != message.EventId || result.Status == OperationStatus.Accepted && result.WorkId != message.WorkId)
                throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
            await results.CompleteAsync(result, token);
            return result;
        }, cancellationToken);
    }
}
