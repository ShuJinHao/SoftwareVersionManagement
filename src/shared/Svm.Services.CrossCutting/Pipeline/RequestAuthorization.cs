using Svm.Services.Contracts.Framework;

namespace Svm.Services.CrossCutting.Pipeline;

internal static class RequestAuthorization
{
    internal static async ValueTask<AuthorizationTarget> CheckAsync(IRequestAuthorizer authorizer, object request,
        RequestPolicy policy, CallContextSnapshot call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = await authorizer.AuthorizeAsync(new(request, policy, call), cancellationToken);
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
        return target;
    }
}
