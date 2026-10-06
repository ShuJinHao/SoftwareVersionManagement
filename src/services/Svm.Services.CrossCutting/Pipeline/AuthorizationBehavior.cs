using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Registration;

namespace Svm.Services.CrossCutting.Pipeline;

public sealed class AuthorizationBehavior<TRequest, TResponse>(
    RequestCatalog catalog, ICallContext context, IRequestAuthorizer? authorizer = null)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = context.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var policy = catalog.GetPolicy(request.GetType());
        if (authorizer is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var decision = await authorizer.AuthorizeAsync(new AuthorizationRequest(request, policy, call), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (decision.Failure is { } failure) throw new RequestRejectedException(failure);
        var target = decision.Target ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        if (target.Scope != policy.Scope) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var actor = call.Actor;
        if (actor.SoftwareId is not null && target.SoftwareId != actor.SoftwareId ||
            target.Scope == RequestScope.Instance && actor.InstanceId is not null && target.InstanceId != actor.InstanceId)
            throw new RequestRejectedException(actor.Kind is ActorKind.Instance or ActorKind.RecoveryGrant
                ? RequestFailure.ResourceNotFound : RequestFailure.PermissionDenied);
        if (target.Scope == RequestScope.InternalWork &&
            (target.Owner != policy.Owner || target.Owner != actor.WorkOwner || target.WorkId != actor.WorkId))
            throw new RequestRejectedException(RequestFailure.PermissionDenied);
        return await next();
    }
}
