using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Pipeline;

namespace Svm.Services.CrossCutting.Idempotency;

internal interface IOperationResultRecovery
{
    Task<OperationResultReference?> FindAsync(object request, RequestPolicy policy, OperationRequestData data,
        OperationIdentity original, CancellationToken cancellationToken);
}

internal sealed class IdempotencyCoordinator(IOperationContext operationContext, ICallContext calls,
    IOperationResultRecovery recovery, IOperationResultStore? store = null, IUnitOfWork? unitOfWork = null,
    IRequestAuthorizer? authorizer = null)
{
    internal async Task<OperationResultReference> ExecuteAsync(object request, RequestPolicy policy,
        OperationRequestData data, Func<CancellationToken, Task<OperationResultReference>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var identity = await BindAsync(request, policy, data, cancellationToken);
        if (await ReadAsync(identity, cancellationToken) is { } existing) return existing;
        if (unitOfWork is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var operationId = Guid.NewGuid();
        try
        {
            return await unitOfWork.ExecuteAsync(operationId, async token =>
            {
                // IAM obtains its authorization guard before storage or business locks.
                var currentTarget = await RequestAuthorization.CheckAsync(authorizer!, request, policy, calls.Current!, token);
                if (OperationDigest.Create(policy, currentTarget, data) != identity.RequestDigest)
                    throw new RequestRejectedException(RequestFailure.PermissionDenied);
                if (!await store!.TryAcquireAsync(operationId, token))
                    return await ReadAsync(identity, token) ?? throw new PersistenceException(PersistenceFailure.DependencyUnavailable, operationId);
                var result = await action(token);
                if (result.OperationId != operationId) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
                await store.CompleteAsync(result, token);
                return result;
            }, cancellationToken);
        }
        catch (PersistenceException error) when (error.Failure == PersistenceFailure.CommitOutcomeUnknown)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            try
            {
                if (await recovery.FindAsync(request, policy, data, identity, cancellationToken) is { } result) return result;
            }
            catch (PersistenceException) { /* Keep the original unknown outcome, never replay the action. */ }
            catch (OperationCanceledException) { /* Cancellation does not prove the commit rolled back. */ }
            throw;
        }
    }

    internal async Task<OperationResultReference?> FindExistingAsync(object request, RequestPolicy policy,
        OperationRequestData data, OperationIdentity original, CancellationToken cancellationToken)
    {
        var identity = await BindAsync(request, policy, data, cancellationToken);
        if (identity.Owner != original.Owner || identity.ActorKind != original.ActorKind || identity.SubjectId != original.SubjectId ||
            identity.Operation != original.Operation || identity.Key != original.Key || identity.RequestDigest != original.RequestDigest)
            throw new RequestRejectedException(RequestFailure.PermissionDenied);
        return await ReadAsync(identity, cancellationToken);
    }

    private async Task<OperationIdentity> BindAsync(object request, RequestPolicy policy, OperationRequestData data, CancellationToken token)
    {
        var call = calls.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        if (policy.Idempotency != IdempotencyMode.OperationResult || policy.Transaction != TransactionMode.DatabaseAtomic ||
            call.EntryKind != policy.Kind || !policy.Actors.Contains(call.Actor.Kind) ||
            authorizer is null || store is null || operationContext is not ScopedOperationContext context)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var target = await RequestAuthorization.CheckAsync(authorizer, request, policy, call, token);
        var identity = new OperationIdentity(policy.Owner, call.Actor, policy.Operation, data.Key, OperationDigest.Create(policy, target, data));
        context.Bind(identity);
        return identity;
    }

    private async Task<OperationResultReference?> ReadAsync(OperationIdentity identity, CancellationToken token)
    {
        var stored = await store!.FindAsync(token);
        if (stored is null) return null;
        if (!string.Equals(stored.RequestDigest, identity.RequestDigest, StringComparison.Ordinal))
            throw new RequestRejectedException(RequestFailure.IdempotencyConflict);
        return stored.Result;
    }
}
