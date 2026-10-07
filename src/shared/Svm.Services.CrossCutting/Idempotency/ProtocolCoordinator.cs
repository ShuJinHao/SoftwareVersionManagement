using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Pipeline;

namespace Svm.Services.CrossCutting.Idempotency;

internal interface IProtocolRecovery
{
    Task<ProtocolResult<TResponse>?> FindAsync<TRequest,TResponse>(TRequest request, RequestPolicy policy, CallActor actor, CancellationToken token) where TRequest : notnull;
}
internal sealed class ProtocolCoordinator(ICallContext calls, IProtocolRecovery recovery, IUnitOfWork? unitOfWork = null, IRequestAuthorizer? authorizer = null)
{
    internal async Task<TResponse> ExecuteAsync<TRequest,TResponse>(TRequest request, RequestPolicy policy,
        IProtocolRequestAdapter<TRequest,TResponse> adapter, Func<Task<TResponse>> action, CancellationToken token) where TRequest : notnull
    {
        var call = calls.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        if (unitOfWork is null || authorizer is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        await RequestAuthorization.CheckAsync(authorizer, request, policy, call, token);
        if (await adapter.FindCommittedAsync(request, token) is { } existing) return existing.Value;
        try
        {
            return await unitOfWork.ExecuteAsync(Guid.NewGuid(), async ct =>
            {
                await RequestAuthorization.CheckAsync(authorizer, request, policy, call, ct);
                if (await adapter.FindCommittedAsync(request, ct) is { } committed) return committed.Value;
                return await action();
            }, token);
        }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        {
            if (token.IsCancellationRequested) throw;
            try { if (await recovery.FindAsync<TRequest,TResponse>(request, policy, call.Actor, token) is { } result) return result.Value; }
            catch (PersistenceException) { }
            catch (OperationCanceledException) { }
            throw;
        }
    }
}
