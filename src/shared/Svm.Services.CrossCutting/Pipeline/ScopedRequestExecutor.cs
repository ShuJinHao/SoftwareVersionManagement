using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Idempotency;

namespace Svm.Services.CrossCutting.Pipeline;

/// <summary>
/// Host-only dispatcher for one background invocation. The new scope obtains its own trusted
/// context source; no ambient HTTP actor or caller-supplied role is copied into it.
/// </summary>
public sealed class ScopedRequestExecutor(IServiceScopeFactory scopeFactory) : IOperationResultRecovery, IIntegrationConsumptionRecovery, IProtocolRecovery
{
    public async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, cancellationToken);
    }

    async Task<OperationResultReference?> IOperationResultRecovery.FindAsync(object request, RequestPolicy policy,
        OperationRequestData data, OperationIdentity original, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        // Reauthenticate through the new scope's host adapter; do not copy a claimed actor into it.
        return await scope.ServiceProvider.GetRequiredService<IdempotencyCoordinator>()
            .FindExistingAsync(request, policy, data, original, cancellationToken);
    }

    async Task<ProtocolResult<TResponse>?> IProtocolRecovery.FindAsync<TRequest,TResponse>(TRequest request, RequestPolicy policy, CallActor actor, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var call = scope.ServiceProvider.GetRequiredService<ICallContext>().Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        if (call.Actor.Kind != actor.Kind || call.Actor.ActorId != actor.ActorId || call.Actor.SoftwareId != actor.SoftwareId || call.Actor.InstanceId != actor.InstanceId)
            throw new RequestRejectedException(RequestFailure.PermissionDenied);
        await RequestAuthorization.CheckAsync(scope.ServiceProvider.GetRequiredService<IRequestAuthorizer>(), request, policy, call, token);
        return await scope.ServiceProvider.GetRequiredService<IProtocolRequestAdapter<TRequest,TResponse>>().FindCommittedAsync(request, token);
    }

    async Task<OperationResultReference?> IIntegrationConsumptionRecovery.FindAsync(IIntegrationEvent message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IIntegrationEventPreflight>().VerifyAsync(message, cancellationToken);
        var stored = await scope.ServiceProvider.GetRequiredService<IOperationResultStore>().FindAsync(cancellationToken);
        if (stored is not null && stored.RequestDigest != Consumption.IntegrationConsumptionPreflight.Digest(message))
            throw new IntegrationConsumptionException(ConsumptionFailure.Conflict);
        return stored?.Result;
    }
}
