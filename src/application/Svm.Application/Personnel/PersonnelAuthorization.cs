using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Instances;
using Svm.Application.Instances;
using Svm.Application.Packages;
using Svm.Services.Contracts.Packages;

namespace Svm.Application.Personnel;

internal sealed class PersonnelAuthorization(IPersonnelService personnel, ISessionProofSource proofSource, IUnitOfWork unitOfWork,
    IPersonnelAdministration? administration = null, ISiteAssets? assets = null, ISoftwareCatalog? software = null, InstanceAuthorization? instanceAuthorization = null, PackageAuthorization? packageAuthorization = null, Svm.Application.Tasks.TaskAuthorization? taskAuthorization = null) : IRequestAuthorizer
{
    public async ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        if (request.Request is SeedPersonnelCommand && request.Context.Actor.Kind == ActorKind.Service &&
            request.Context.EntryKind == RequestKind.Internal && request.Context.Actor.WorkOwner == ModuleOwner.Identity)
            return request.Context.Actor.WorkId is { } workId ? AuthorizationDecision.Allow(AuthorizationTarget.Work(ModuleOwner.Identity, workId)) :
                AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        if (Svm.Services.Contracts.Tasks.TaskCapabilities.Contains(request.Request.GetType()) && request.Context.Actor.Kind is ActorKind.Service or ActorKind.Instance)
            return taskAuthorization is null ? AuthorizationDecision.Deny(RequestFailure.ConfigurationInvalid) : await taskAuthorization.AuthorizeAsync(request, null, cancellationToken);
        if (PackageCapabilities.IsWrite(request.Request.GetType()) || PackageCapabilities.IsQuery(request.Request.GetType()))
        {
            if (packageAuthorization is null) return AuthorizationDecision.Deny(RequestFailure.ConfigurationInvalid);
            if (request.Context.Actor.Kind == ActorKind.Service) return await packageAuthorization.InternalAsync(request, cancellationToken);
            if (request.Context.Actor.Kind == ActorKind.Instance) return await packageAuthorization.ExternalAsync(request, null, cancellationToken);
        }
        if (InstanceCapabilities.IsWrite(request.Request.GetType()) || InstanceCapabilities.IsQuery(request.Request.GetType()))
            if (request.Context.Actor.Kind is ActorKind.Instance or ActorKind.EnrollmentGrant or ActorKind.RecoveryGrant)
                return instanceAuthorization is null ? AuthorizationDecision.Deny(RequestFailure.ConfigurationInvalid) : await instanceAuthorization.MachineAsync(request, cancellationToken);
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
        if (Svm.Services.Contracts.Tasks.TaskCapabilities.Contains(request.Request.GetType()))
            return taskAuthorization is null ? AuthorizationDecision.Deny(RequestFailure.ConfigurationInvalid) : await taskAuthorization.AuthorizeAsync(request, person, cancellationToken);
        if (PackageCapabilities.IsWrite(request.Request.GetType()) || PackageCapabilities.IsQuery(request.Request.GetType()))
            return await packageAuthorization!.ExternalAsync(request, person, cancellationToken);
        if (InstanceCapabilities.IsWrite(request.Request.GetType()) || InstanceCapabilities.IsQuery(request.Request.GetType()))
            return instanceAuthorization is null ? AuthorizationDecision.Deny(RequestFailure.ConfigurationInvalid) : await instanceAuthorization.HumanAsync(request, person, cancellationToken);
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
