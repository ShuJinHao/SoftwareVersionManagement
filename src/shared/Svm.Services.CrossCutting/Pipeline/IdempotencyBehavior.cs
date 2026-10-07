using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Idempotency;
using Svm.Services.CrossCutting.Registration;

namespace Svm.Services.CrossCutting.Pipeline;

internal sealed class IdempotencyBehavior<TRequest, TResponse>(RequestCatalog catalog, IdempotencyCoordinator coordinator,
    IIdempotencyRequestAdapter<TRequest, TResponse>? adapter = null) : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var policy = catalog.GetPolicy(request.GetType());
        if (policy.Idempotency == IdempotencyMode.None) return await next();
        if (policy.Idempotency != IdempotencyMode.OperationResult || adapter is null)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        OperationResultReference? executedReference = null;
        TResponse response = default!;
        var reference = await coordinator.ExecuteAsync(request, policy, adapter.Describe(request), async _ =>
        {
            response = await next();
            executedReference = adapter.GetReference(response);
            return executedReference;
        }, cancellationToken);
        // Confirmation may observe a different request's committed result for the same key.
        return executedReference == reference ? response : await adapter.RestoreAsync(reference, cancellationToken);
    }
}
