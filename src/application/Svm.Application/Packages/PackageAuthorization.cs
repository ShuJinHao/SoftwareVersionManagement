using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Packages;

namespace Svm.Application.Packages;

internal sealed class PackageAuthorization(IPackages packages, IReleases releases, ISoftwareCatalog software,
    IUnitOfWork unit, IPersonnelWorkAuthorization personnelWork, IPackageServiceIdentity? service = null,
    IInstanceAccess? access = null, IManagedInstances? instances = null, IAccessProofSource? machineProof = null)
{
    public async ValueTask<AuthorizationDecision> ExternalAsync(AuthorizationRequest x, PersonnelView? person, CancellationToken token)
    {
        if (x.Request is GetPackageCapabilitiesQuery && person is not null)
            return AuthorizationDecision.Allow(AuthorizationTarget.Global());
        var write = unit.CurrentOperationId is not null;
        var id = await SoftwareAsync(x.Request, token);
        if (id is null || id == Guid.Empty) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if (x.Context.Actor.Kind == ActorKind.Instance)
        {
            var identity = await (access ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid)).AuthenticateAsync(machineProof?.Proof ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid), write, token);
            if (identity?.SoftwareId != id || identity.InstanceId != x.Context.Actor.InstanceId) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            if ((await (instances ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid)).GetIdentityAsync(identity.InstanceId!.Value, write, token))?.Lifecycle != "Active") return AuthorizationDecision.Deny(RequestFailure.InstanceSuspended);
        }
        else if (person is null || !person.Permissions.Any(p => p.SoftwareId == id && p.Operation == x.Policy.Permission))
            return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if (!await software.ExistsAsync(id.Value, write, token)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        // REL precedes PKG locks for every cross-module operation.
        var release = await ReleaseAsync(x.Request, token);
        if (release is { } r) await releases.GetAsync(r, write, token);
        return AuthorizationDecision.Allow(AuthorizationTarget.Software(id.Value));
    }
    public async ValueTask<AuthorizationDecision> InternalAsync(AuthorizationRequest x, CancellationToken token)
    {
        if (service is null || x.Context.Actor.ActorId != service.SubjectId || !PackageCapabilities.IsInternal(x.Request.GetType()))
            return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        var protect = unit.CurrentOperationId is not null; Guid target; Guid? softwareId = null;
        switch (x.Request)
        {
            case AuthorizeDownloadCommand a when service.Role == "Gateway" && service.NodeId == a.NodeId:
                target = a.PackageId; softwareId = await packages.SoftwareForAsync("package", target, false, token); break;
            case RecordDownloadEndCommand e when service.Role == "Collector" && service.NodeId == e.End.NodeId:
                target = e.End.RequestId; break;
            case GetDownloadEndQuery e when service.Role == "Collector" && service.NodeId == e.NodeId:
                target = e.RequestId; break;
            case CheckPackageReplicasCommand c when service.Role is "Executor" or "Gateway":
                target = c.PackageId; softwareId = await packages.SoftwareForAsync("package", target, false, token);
                if (softwareId is { } sid) await ProtectPackage(c.PackageId, sid, protect, token);
                break;
            case InspectReplicaQuery i when service.Role is "Executor" or "Peer" or "Gateway":
                target = i.PackageId; softwareId = await packages.SoftwareForAsync("package", target, false, token); break;
            default:
                if (service.Role is not ("Executor" or "Peer")) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
                target = WorkId(x.Request);
                if (target == Guid.Empty || service.Role == "Peer" && x.Request is not GetPackageAuthorityQuery)
                    return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
                var w = await packages.AuthorityAsync(target, false, token); softwareId = w.SoftwareId;
                if (w.Kind != "Repair" && !await personnelWork.HasPermissionAsync(w.InitiatorId, w.SoftwareId, "release.upload", protect, token))
                    return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
                await ProtectPackage(w.PackageId, w.SoftwareId, protect, token);
                if ((await releases.GetAsync(w.ReleaseId, protect, token)).State == "Disabled") return AuthorizationDecision.Deny(RequestFailure.InvalidState);
                if (x.Request is ClaimPackageWorkCommand claim && claim.NodeId != service.NodeId) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
                break;
        }
        return AuthorizationDecision.Allow(AuthorizationTarget.Work(ModuleOwner.Packages, target, softwareId));
    }
    private async Task ProtectPackage(Guid id, Guid softwareId, bool protect, CancellationToken token)
    {
        if (!await software.ExistsAsync(softwareId, protect, token)) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var p = await packages.GetAsync(id, false, false, token); await releases.GetAsync(p.ReleaseId, protect, token);
    }
    private async Task<Guid?> SoftwareAsync(object x, CancellationToken token) => x switch
    {
        CreateReleaseCommand c => c.SoftwareId, ListReleasesQuery q => q.Input.SoftwareId, ListClientReleasesQuery q => q.Input.SoftwareId,
        GetDownloadAuditQuery q => q.SoftwareId,
        _ when await ReleaseAsync(x, token) is { } release => await releases.SoftwareForAsync(release, false, token),
        _ => await packages.SoftwareForAsync(IsUpload(x) ? "work" : "package", ResourceId(x), false, token)
    };
    private async Task<Guid?> ReleaseAsync(object x, CancellationToken token) => x switch
    {
        PublishReleaseCommand c => c.ReleaseId, DisableReleaseCommand c => c.ReleaseId, GetReleaseQuery q => q.ReleaseId, GetClientReleaseQuery q => q.ReleaseId, GetTestEvidenceQuery q => q.ReleaseId,
        GetPackageQuery q => (await packages.GetAsync(q.PackageId, q.Upload, false, token)).ReleaseId,
        GetClientPackageQuery q => (await packages.GetAsync(q.PackageId, false, false, token)).ReleaseId,
        RetryPackageCommand c => (await packages.GetAsync(c.PackageId, false, false, token)).ReleaseId,
        UploadContentCommand c => (await packages.GetAsync(c.UploadId, true, false, token)).ReleaseId,
        BeginUploadCommand c => (await packages.GetAsync(c.UploadId, true, false, token)).ReleaseId,
        FinishUploadCommand c => (await packages.GetAsync(c.Receipt.UploadId, true, false, token)).ReleaseId,
        FailUploadCommand c => (await packages.GetAsync(c.UploadId, true, false, token)).ReleaseId,
        CheckUploadQuery c => (await packages.GetAsync(c.UploadId, true, false, token)).ReleaseId,
        GetUploadTargetQuery c => (await packages.GetAsync(c.UploadId, true, false, token)).ReleaseId,
        GetPackageWorkQuery c => (await packages.AuthorityAsync(c.WorkId, false, token)).ReleaseId,
        _ => null
    };
    private static bool IsUpload(object x) => x is UploadContentCommand or BeginUploadCommand or FinishUploadCommand or FailUploadCommand or CheckUploadQuery or GetPackageWorkQuery or GetUploadTargetQuery || x is GetPackageQuery { Upload: true };
    private static Guid ResourceId(object x) => x switch
    {
        RetryPackageCommand c => c.PackageId, UploadContentCommand c => c.UploadId, BeginUploadCommand c => c.UploadId,
        FinishUploadCommand c => c.Receipt.UploadId, FailUploadCommand c => c.UploadId, CheckUploadQuery q => q.UploadId, GetUploadTargetQuery q => q.UploadId,
        GetPackageQuery q => q.PackageId, GetClientPackageQuery q => q.PackageId, GetPackageWorkQuery q => q.WorkId, _ => Guid.Empty
    };
    private static Guid WorkId(object x) => x switch
    {
        ClaimPackageWorkCommand c => c.WorkId, RenewPackageWorkCommand c => c.Lease.Work.WorkId, CompletePackageWorkCommand c => c.Lease.Work.WorkId,
        FailPackageWorkCommand c => c.Lease.Work.WorkId, GetPackageAuthorityQuery q => q.WorkId, _ => Guid.Empty
    };
}

internal sealed class PackageWorkerAuthorization(PackageAuthorization authorization) : IRequestAuthorizer
{
    public ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest x,CancellationToken t) =>
        PackageCapabilities.IsWorker(x.Request.GetType()) ? authorization.InternalAsync(x,t) : ValueTask.FromResult(AuthorizationDecision.Deny(RequestFailure.PermissionDenied));
}
