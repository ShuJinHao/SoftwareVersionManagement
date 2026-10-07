using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Catalog;

namespace Svm.Application.Personnel;

internal sealed class PersonnelAuthorization(IPersonnelService personnel, ISessionProofSource proofSource, IUnitOfWork unitOfWork,
    IPersonnelAdministration? administration = null, ISiteAssets? assets = null, ISoftwareCatalog? software = null) : IRequestAuthorizer
{
    public async ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        if (request.Request is SeedPersonnelCommand && request.Context.Actor.Kind == ActorKind.Service &&
            request.Context.EntryKind == RequestKind.Internal && request.Context.Actor.WorkOwner == ModuleOwner.Identity)
            return request.Context.Actor.WorkId is { } workId ? AuthorizationDecision.Allow(AuthorizationTarget.Work(ModuleOwner.Identity, workId)) :
                AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        if (request.Context.Actor.Kind == ActorKind.Anonymous)
            return request.Request is AnonymousSessionQuery or LoginCommand ? AuthorizationDecision.Allow(AuthorizationTarget.Global()) :
                AuthorizationDecision.Deny(RequestFailure.AuthenticationRequired);
        if (request.Context.Actor.Kind != ActorKind.Human || proofSource.Proof is not { } proof || proof.SubjectId != request.Context.Actor.ActorId)
            return AuthorizationDecision.Deny(RequestFailure.AuthenticationRequired);
        if ((PersonnelManagementCapabilities.Contains(request.Request.GetType()) || request.Request is CreateSoftwareCommand) && unitOfWork.CurrentOperationId is not null)
        {
            if (administration is null) return AuthorizationDecision.Deny(RequestFailure.ConfigurationInvalid);
            await administration.ProtectAsync(proof.SubjectId, request.Request is CreateSoftwareCommand ? null : PersonnelManagementCapabilities.Target(request.Request), cancellationToken);
        }
        var person = await personnel.AuthenticateAsync(proof, unitOfWork.CurrentOperationId is not null, cancellationToken);
        if (person is null) return AuthorizationDecision.Deny(RequestFailure.AuthenticationRequired);
        if (request.Request is CurrentSessionQuery or ChangePasswordCommand or LogoutCommand)
            return AuthorizationDecision.Allow(AuthorizationTarget.Global());
        if (person.MustChangePassword) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        if (CatalogCapabilities.IsWrite(request.Request.GetType()) || CatalogCapabilities.IsQuery(request.Request.GetType()))
        {
            if (assets is null || software is null) return AuthorizationDecision.Deny(RequestFailure.ConfigurationInvalid);
            var target = CatalogCapabilities.SoftwareTarget(request.Request);
            if (request.Request is CreateBindingCommand or RevokeBindingCommand &&
                !person.Permissions.Any(p => p.SoftwareId is null && p.Operation == "asset.manage"))
                return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
            if (target is { } softwareId)
            {
                if (!person.Permissions.Any(p => p.SoftwareId == softwareId && p.Operation == request.Policy.Permission))
                    return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
                await assets.EnsureDeploymentAsync(unitOfWork.CurrentOperationId is not null, cancellationToken);
                if (!await software.ExistsAsync(softwareId, unitOfWork.CurrentOperationId is not null, cancellationToken))
                    return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
                return AuthorizationDecision.Allow(AuthorizationTarget.Software(softwareId));
            }
            if (request.Request is not ListSoftwareQuery && !person.Permissions.Any(p => p.SoftwareId is null && p.Operation == request.Policy.Permission))
                return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
            await assets.EnsureDeploymentAsync(unitOfWork.CurrentOperationId is not null, cancellationToken);
            return AuthorizationDecision.Allow(AuthorizationTarget.Global());
        }
        if (request.Policy.Scope != RequestScope.Global) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        return person.Permissions.Any(p => p.SoftwareId is null && p.Operation == request.Policy.Permission)
            ? AuthorizationDecision.Allow(AuthorizationTarget.Global()) : AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
    }
}
