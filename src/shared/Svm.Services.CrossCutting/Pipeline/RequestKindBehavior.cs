using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Registration;

namespace Svm.Services.CrossCutting.Pipeline;

public sealed class RequestKindBehavior<TRequest, TResponse>(RequestCatalog catalog, ICallContext context)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = catalog.GetPolicy(request.GetType());
        var call = context.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        if (call.Actor.Kind == ActorKind.Anonymous && !policy.Actors.Contains(ActorKind.Anonymous))
            throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        if (call.EntryKind != policy.Kind)
            throw new RequestRejectedException(RequestFailure.PermissionDenied);
        if (!policy.Actors.Contains(call.Actor.Kind))
            throw new RequestRejectedException(policy.Actors.Count == 1 && policy.Actors[0] == ActorKind.Human
                ? RequestFailure.HumanRequired : RequestFailure.PermissionDenied);
        if (call.Actor.Kind == ActorKind.Service && call.Actor.WorkOwner != policy.Owner)
            throw new RequestRejectedException(RequestFailure.PermissionDenied);
        return next();
    }
}
