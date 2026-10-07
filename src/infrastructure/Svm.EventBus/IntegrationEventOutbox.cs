using MassTransit;
using Svm.EntityFrameworkCore.Messaging;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;

namespace Svm.EventBus;

internal sealed class IntegrationEventOutbox(ISendEndpointProvider endpoints, IOutboxTransactionGuard transactions,
    IOperationContext operations, MessagingOptions options) : IIntegrationEventOutbox
{
    public async Task EnqueueAsync<TEvent>(TEvent message, CancellationToken cancellationToken) where TEvent : class, IIntegrationEvent
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        transactions.EnsureCanEnqueue();
        var owner = message switch
        {
            PackageWorkAvailableV1 p when Enum.IsDefined(p.WorkKind) => ModuleOwner.Packages,
            TaskPreparationAvailableV1 p when Enum.IsDefined(p.WorkKind) => ModuleOwner.Tasks,
            TaskControlAvailableV1 p when Enum.IsDefined(p.WorkKind) => ModuleOwner.Tasks,
            _ => throw new OutboxException(OutboxFailure.InvalidMessage)
        };
        if (operations.Current?.Owner != owner) throw new OutboxException(OutboxFailure.InvalidOwnership);
        if (message.EventId == Guid.Empty || message.CorrelationId == Guid.Empty || message.CausationId == Guid.Empty ||
            message.SiteId != options.SiteId || message.SoftwareId == Guid.Empty || message.WorkId == Guid.Empty ||
            message.OccurredAt == default || message.OccurredAt.Offset != TimeSpan.Zero || message.DispatchSequence < 1)
            throw new OutboxException(OutboxFailure.InvalidMessage);
        // Runtime type is narrowed to the whitelist; generic interfaces never become wire contracts.
        switch (message)
        {
            case PackageWorkAvailableV1 p: await StageAsync(p, cancellationToken); break;
            case TaskPreparationAvailableV1 p: await StageAsync(p, cancellationToken); break;
            case TaskControlAvailableV1 p: await StageAsync(p, cancellationToken); break;
        }
    }

    private async Task StageAsync<T>(T message, CancellationToken token) where T : class, IIntegrationEvent
    {
        var endpoint = await endpoints.GetSendEndpoint(new Uri("queue:" + message.MessageType));
        token.ThrowIfCancellationRequested();
        await endpoint.Send(message, context =>
        {
            context.MessageId = message.EventId; context.CorrelationId = message.CorrelationId;
            context.InitiatorId = message.CausationId; context.Durable = true;
        }, token);
    }
}
