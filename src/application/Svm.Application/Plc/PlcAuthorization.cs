using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Plc;
using Svm.Application.Personnel;
namespace Svm.Application.Plc;

internal sealed class PlcAuthorization(IPlcUploadAccess credentials, IPlcPublication publication, IHardwareAssets hardware,
    ISoftwareCatalog catalog, IReleases releases, IPackages packages, SiteAuthorization scope, IUnitOfWork unit,
    IAccessProofSource proofs, ISiteAssets sites)
{
    internal async Task<Svm.Services.Contracts.Catalog.HardwareBindingView> Position(Guid id, long? revision, Guid? software, bool protect, CancellationToken token)
    {
        var p = await hardware.BindingAsync(id, protect, token);
        if (software is { } sid && p.SoftwareId != sid) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (!p.IsActive || (await hardware.GetAsync(p.HardwareId, false, token)).Type != "PLC" || !await sites.HasActiveBindingAsync(p.DeviceId,p.SoftwareId,token))
            throw new RequestRejectedException(RequestFailure.InvalidState);
        if (revision is { } expected && p.Revision != expected) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        return p;
    }
    public async ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest x, PersonnelView? person, CancellationToken token)
    {
        var protect = unit.CurrentOperationId is not null;
        await sites.EnsureDeploymentAsync(protect, token);
        if (x.Context.Actor.Kind == ActorKind.PlcUpload)
        {
            if (!PlcCapabilities.IsUpload(x.Request.GetType())) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
            var credential = await credentials.AuthenticateAsync(proofs.Proof ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid), protect, token);
            if (credential is null || credential.Id != x.Context.Actor.ActorId || credential.SoftwareId != x.Context.Actor.SoftwareId)
                return AuthorizationDecision.Deny(RequestFailure.CredentialInvalid);
            var product = await catalog.GetAsync(credential.SoftwareId, protect, token);
            if (product.Category != "PLC") return AuthorizationDecision.Deny(RequestFailure.InvalidState);
            PlcScope origin;
            if (x.Request is CreatePlcArchiveCommand create) origin = new(create.BindingId, create.BindingRevision);
            else
            {
                var workId = x.Request switch { PlcUploadContentCommand c => c.UploadId, PlcBeginUploadCommand c => c.UploadId,
                    PlcFinishUploadCommand c => c.Receipt.UploadId, PlcFailUploadCommand c => c.UploadId, PlcCheckUploadQuery c => c.UploadId,
                    PlcGetUploadTargetQuery c => c.UploadId, PlcGetPackageWorkQuery c => c.WorkId,
                    PlcGetPackageQuery c => c.Upload ? c.PackageId : await packages.UploadIdAsync(c.PackageId, token),
                    PlcRetryPackageCommand c => await packages.UploadIdAsync(c.PackageId, token), _ => Guid.Empty };
                var work = await packages.AuthorityAsync(workId, false, token);
                var release = await releases.GetAsync(work.ReleaseId, protect, token);
                if (release.SoftwareId != credential.SoftwareId || release.CreatedBy != credential.Id || work.InitiatorId != credential.Id)
                    return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
                origin = await publication.UploadScopeAsync(release.Id, token) ?? throw new RequestRejectedException(RequestFailure.InvalidState);
            }
            if (!credential.Scope.Contains(origin)) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
            await Position(origin.BindingId, origin.BindingRevision, credential.SoftwareId, protect, token);
            return AuthorizationDecision.Allow(AuthorizationTarget.Software(credential.SoftwareId));
        }
        if (person is null) return AuthorizationDecision.Deny(RequestFailure.AuthenticationRequired);
        var id = x.Request switch { CreatePlcCredentialCommand c => c.SoftwareId, ListPlcCredentialsQuery q => q.SoftwareId,
            RevokePlcCredentialCommand c => (await credentials.GetAsync(c.CredentialId, protect, token)).SoftwareId,
            PublishPlcReleaseCommand c => (await releases.GetAsync(c.ReleaseId, false, token)).SoftwareId,
            GetPlcEvidenceQuery q => (await releases.GetAsync(q.ReleaseId, false, token)).SoftwareId,
            RecordPlcUsageCommand c => (await hardware.BindingAsync(c.BindingId, false, token)).SoftwareId,
            GetPlcUsageQuery q => (await hardware.BindingAsync(q.BindingId, false, token)).SoftwareId, _ => Guid.Empty };
        if (x.Request is ListPlcUsageQuery list)
        {
            var h = await hardware.GetAsync(list.HardwareId, false, token);
            return h.Type == "PLC" && await scope.DeviceAsync(person.Access,"asset.read",h.DeviceId,null,token)
                ? AuthorizationDecision.Allow(AuthorizationTarget.Global()) : AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        }
        if (!await scope.SoftwareAsync(person.Access, x.Policy.Permission!, id, token)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if ((await catalog.GetAsync(id, protect, token)).Category != "PLC") return AuthorizationDecision.Deny(RequestFailure.InvalidState);
        if (x.Request is CreatePlcCredentialCommand grant)
            foreach (var s in grant.Scope.OrderBy(s => s.BindingId))
            {
                var p = await hardware.BindingAsync(s.BindingId,protect,token);
                if (p.SoftwareId != id || !await scope.DeviceAsync(person.Access,"asset.read",p.DeviceId,id,token)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            }
        var releaseId = x.Request switch { PublishPlcReleaseCommand c => c.ReleaseId, RecordPlcUsageCommand c => c.ReleaseId, _ => (Guid?)null };
        if (releaseId is { } rid && (await releases.GetAsync(rid,protect,token)).SoftwareId != id)
            return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if (x.Request is GetPlcEvidenceQuery evidence && await publication.GetAsync(evidence.ReleaseId,token) is { } fact)
        {
            var h = await hardware.GetAsync(fact.HardwareId,false,token);
            if (!await scope.DeviceAsync(person.Access,"asset.read",h.DeviceId,id,token) ||
                !await scope.DeviceAsync(person.Access,"software.read",h.DeviceId,id,token)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        }
        var bindingId = x.Request switch { PublishPlcReleaseCommand c => c.BindingId, RecordPlcUsageCommand c => c.BindingId, GetPlcUsageQuery q => q.BindingId, _ => (Guid?)null };
        if (bindingId is { } binding)
        {
            // Preserve current-authorization-before-replay. Revision/state are checked only in the handler.
            var p = await hardware.BindingAsync(binding, protect, token);
            if (p.SoftwareId != id || !await scope.DeviceAsync(person.Access, x.Request is PublishPlcReleaseCommand ? "plc.verify" : x.Policy.Permission!, p.DeviceId, id, token))
                return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        }
        return AuthorizationDecision.Allow(AuthorizationTarget.Software(id));
    }
}
