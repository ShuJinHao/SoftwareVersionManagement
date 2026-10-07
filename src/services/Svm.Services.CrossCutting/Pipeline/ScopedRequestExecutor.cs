using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Idempotency;

namespace Svm.Services.CrossCutting.Pipeline;

/// <summary>
/// Host-only dispatcher for one background invocation. The new scope obtains its own trusted
/// context source; no ambient HTTP actor or caller-supplied role is copied into it.
/// </summary>
public sealed class ScopedRequestExecutor(IServiceScopeFactory scopeFactory) : IOperationResultRecovery
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
}
