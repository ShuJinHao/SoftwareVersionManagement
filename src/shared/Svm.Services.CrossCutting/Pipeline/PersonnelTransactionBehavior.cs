using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Instances;

namespace Svm.Services.CrossCutting.Pipeline;

public sealed class PersonnelTransactionBehavior<TRequest, TResponse>(RequestCatalog catalog, ICallContext context,
    IUnitOfWork? unitOfWork = null, IRequestAuthorizer? authorizer = null) : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var policy = catalog.GetPolicy(request.GetType());
        if (policy.Transaction == TransactionMode.ReadOnly) return await next();
        if (PersonnelManagementCapabilities.Contains(request.GetType()) || CatalogCapabilities.IsWrite(request.GetType()) || InstanceCapabilities.IsWrite(request.GetType()))
        {
            // The idempotency coordinator already owns and reauthorizes this root transaction.
            if (policy.Idempotency is not (IdempotencyMode.OperationResult or IdempotencyMode.EnrollmentProtocol or IdempotencyMode.ReportSequence) || unitOfWork?.CurrentOperationId is null)
                throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
            return await next();
        }
        if (!PersonnelWriteCapabilities.Contains(request.GetType()) || unitOfWork is null || authorizer is null)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        return await unitOfWork.ExecuteAsync(Guid.NewGuid(), async token =>
        {
            var decision = await authorizer.AuthorizeAsync(new AuthorizationRequest(request, policy,
                context.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired)), token);
            if (decision.Failure is { } failure) throw new RequestRejectedException(failure);
            return await next();
        }, cancellationToken);
    }
}
