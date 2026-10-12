using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.CrossCutting.Idempotency;

namespace Svm.Services.CrossCutting.Pipeline;

public sealed class PersonnelTransactionBehavior<TRequest, TResponse>(RequestCatalog catalog, ICallContext context,
    IUnitOfWork? unitOfWork = null, IRequestAuthorizer? authorizer = null, IOperationContext? operations = null) : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var policy = catalog.GetPolicy(request.GetType());
        if (policy.Transaction == TransactionMode.ReadOnly) return await next();
        if (request is UploadContentCommand)
        {
            if (unitOfWork is null || unitOfWork.CurrentOperationId is not null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
            return await next();
        }
        if (PersonnelManagementCapabilities.Contains(request.GetType()) || CatalogCapabilities.IsWrite(request.GetType()) || InstanceCapabilities.IsWrite(request.GetType()) || PackageCapabilities.IsIdempotent(request.GetType()))
        {
            // The idempotency coordinator already owns and reauthorizes this root transaction.
            if (policy.Idempotency is not (IdempotencyMode.OperationResult or IdempotencyMode.EnrollmentProtocol or IdempotencyMode.ReportSequence) || unitOfWork?.CurrentOperationId is null)
                throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
            return await next();
        }
        if (!PersonnelWriteCapabilities.Contains(request.GetType()) && !PackageCapabilities.IsInternal(request.GetType()) || unitOfWork is null || authorizer is null)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var operationId = Guid.NewGuid();
        return await unitOfWork.ExecuteAsync(operationId, async token =>
        {
            var decision = await authorizer.AuthorizeAsync(new AuthorizationRequest(request, policy,
                context.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired)), token);
            if (decision.Failure is { } failure) throw new RequestRejectedException(failure);
            if (PackageCapabilities.IsInternal(request.GetType()))
            {
                var target = decision.Target ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
                if (operations is not ScopedOperationContext scoped) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
                var data = new OperationRequestData(operationId, OperationValue.Object(), OperationValue.Object());
                scoped.Bind(new OperationIdentity(policy.Owner, context.Current!.Actor, policy.Operation, operationId, OperationDigest.Create(policy, target, data)));
            }
            return await next();
        }, cancellationToken);
    }
}
