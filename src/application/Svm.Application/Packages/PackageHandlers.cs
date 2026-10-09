using MediatR;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Messaging.V1;
using Svm.Application.Catalog;

namespace Svm.Application.Packages;

internal sealed class PackageDispatch(SiteCatalogOptions site, TimeProvider clock)
{
    public PackageWorkAvailableV1 Create(PackageWorkAuthority w) => new(
        w.DispatchEventId, clock.GetUtcNow(), w.WorkId, null, site.Require().SiteId, w.SoftwareId,
        w.Kind == "Repair" ? PackageWorkKind.Repair : PackageWorkKind.UploadVerificationAndCopy, w.WorkId, w.DispatchSequence);
}
internal sealed class CreateReleaseCommandHandler(IReleases releases, IPackages packages, ICallContext calls, CatalogCompletion completion)
    : IRequestHandler<CreateReleaseCommand, OperationResult<ReleaseUploadResult>>
{
    public Task<OperationResult<ReleaseUploadResult>> Handle(CreateReleaseCommand x, CancellationToken token) =>
        completion.ExecuteAsync("rel.release.create", x.ChangeReason, async () =>
        {
            var actor = calls.Current!.Actor.ActorId!.Value; var packageId = Guid.NewGuid();
            var release = await releases.CreateAsync(x, packageId, actor, token);
            var uploadId = await packages.CreateAsync(packageId, release.Id, release.SoftwareId, actor, x.Package, token);
            return new ReleaseUploadResult(release, uploadId, $"/api/v1/manage/uploads/{uploadId:D}/content");
        }, x => x.Release.Id, token);
}
internal sealed class DisableReleaseCommandHandler(IReleases releases, IPackages packages, CatalogCompletion completion)
    : IRequestHandler<DisableReleaseCommand, OperationResult<ReleaseView>>
{
    public Task<OperationResult<ReleaseView>> Handle(DisableReleaseCommand x, CancellationToken token) => completion.ExecuteAsync(
        "rel.release.disable", x.Reason, async () => { var r = await releases.DisableAsync(x.ReleaseId, x.ExpectedRevision, x.Reason, token); await packages.StopAsync(r.PackageId, token); return r; }, x => x.Id, token);
}
internal sealed class PublishReleaseCommandHandler(IReleases releases, IPackages packages, IManagedInstances instances, CatalogCompletion completion)
    : IRequestHandler<PublishReleaseCommand, OperationResult<ReleaseView>>
{
    public Task<OperationResult<ReleaseView>> Handle(PublishReleaseCommand x, CancellationToken token) =>
        completion.ExecuteAsync("rel.release.publish", x.PublishReason, async actor =>
        {
            var release = await releases.GetAsync(x.ReleaseId, true, token);
            if (release.Revision != x.ExpectedRevision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
            if (release.State != "Test") throw new RequestRejectedException(RequestFailure.InvalidState);
            await instances.VerifyInstallationEvidenceAsync(x.TestEvidenceId, release.SoftwareId, release.Id, release.Version, token);
            await packages.VerifyPublishReadyAsync(release.PackageId, release.Id, release.SoftwareId, token);
            var published = await releases.PublishAsync(x, actor.SubjectId, actor.EmployeeNo, token);
            return published with { DownloadAvailable = true };
        }, release => release.Id, token);
}
internal sealed class RetryPackageCommandHandler(IPackages packages, ICallContext calls, PackageDispatch dispatch, IIntegrationEventOutbox outbox, CatalogCompletion completion)
    : IRequestHandler<RetryPackageCommand, OperationResult<PackageView>>
{
    public Task<OperationResult<PackageView>> Handle(RetryPackageCommand x, CancellationToken token) => completion.ExecuteAsync(
        "pkg.package.retry", x.Reason, async () => { var w = await packages.RetryAsync(x.PackageId, x.ExpectedRevision, calls.Current!.Actor.ActorId!.Value, token); await outbox.EnqueueAsync(dispatch.Create(w), token); return await packages.GetAsync(x.PackageId, false, false, token); }, x => x.Id, token);
}
internal sealed class BeginUploadCommandHandler(IPackages packages, IUnitOfWork unit) : IRequestHandler<BeginUploadCommand, OperationResult<UploadReceipt>>
{
    public async Task<OperationResult<UploadReceipt>> Handle(BeginUploadCommand x, CancellationToken token)
    { var r = await packages.BeginAsync(x.UploadId, x.ReceiveToken, x.NodeId, token); return OperationResult<UploadReceipt>.Completed(unit.CurrentOperationId!.Value, r, r.PackageId); }
}
internal sealed class FinishUploadCommandHandler(IPackages packages, PackageDispatch dispatch, IIntegrationEventOutbox outbox, CatalogCompletion completion)
    : IRequestHandler<FinishUploadCommand, OperationResult<PackageView>>
{
    public Task<OperationResult<PackageView>> Handle(FinishUploadCommand x, CancellationToken token) => completion.ExecuteAsync(
        "pkg.upload.received", "size and SHA-256 verified", async () => { var w = await packages.FinishAsync(x.Receipt, x.Size, x.Sha256, token); await outbox.EnqueueAsync(dispatch.Create(w), token); return await packages.GetAsync(w.PackageId, false, false, token); }, x => x.Id, token);
}
internal sealed class FailUploadCommandHandler(IPackages packages, CatalogCompletion completion) : IRequestHandler<FailUploadCommand, OperationResult<PackageView>>
{
    public Task<OperationResult<PackageView>> Handle(FailUploadCommand x, CancellationToken token) => completion.ExecuteAsync("pkg.upload.failed", x.Code,
        async () => { await packages.FailUploadAsync(x.UploadId, x.ReceiveToken, x.Code, token); return await packages.GetAsync(x.UploadId, true, false, token); }, x => x.Id, token);
}
internal sealed class CheckUploadQueryHandler(IPackages packages) : IRequestHandler<CheckUploadQuery, UploadReceipt>
{
    public async Task<UploadReceipt> Handle(CheckUploadQuery x, CancellationToken token)
    { var r = await packages.ReceivingAsync(x.UploadId, x.ReceiveToken, token); if (r.AlreadyReceived) throw new RequestRejectedException(RequestFailure.InvalidState); return r; }
}
internal sealed class UploadContentCommandHandler(IPackageFiles files, ICommandScopeExecutor phases, IPackages packages)
    : IRequestHandler<UploadContentCommand, OperationResult<PackageView>>
{
    public async Task<OperationResult<PackageView>> Handle(UploadContentCommand x, CancellationToken token)
    {
        await using var mutex = await files.LockUploadAsync(x.UploadId, token);
        var receiveToken = Guid.NewGuid();
        var begin = await phases.ExecuteAsync(new BeginUploadCommand(x.UploadId, receiveToken, files.NodeId), token);
        if (begin.Value.AlreadyReceived) return OperationResult<PackageView>.Completed(begin.OperationId, await packages.GetAsync(x.UploadId, true, false, token), begin.Value.PackageId);
        (long Size, string Sha256) received;
        try
        {
            received = await files.ReceiveAsync(begin.Value, x.Content, x.ContentLength,
                async ct => { await phases.QueryAsync(new CheckUploadQuery(x.UploadId, receiveToken), ct); }, token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or RequestRejectedException)
        {
            // A separate cancellation budget can record an interrupted receive. It cannot grant new authority.
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await phases.ExecuteAsync(new FailUploadCommand(x.UploadId, receiveToken,
                error is RequestRejectedException rejected ? rejected.Code : "UPLOAD_INTERRUPTED"), budget.Token); }
            catch (Exception failure) when (failure is RequestRejectedException or PersistenceException or OperationCanceledException) { }
            throw;
        }
        // Do not turn a commit-unknown result into a new receive or another sending intent.
        return await phases.ExecuteAsync(new FinishUploadCommand(begin.Value, received.Size, received.Sha256), token);
    }
}
internal sealed class PackageWorkAvailableHandler(IPackages packages) : IIntegrationEventHandler<PackageWorkAvailableV1>
{
    public async Task<OperationResultReference> HandleAsync(PackageWorkAvailableV1 message, IntegrationConsumptionContext context, CancellationToken token)
    {
        await packages.AcceptAsync(message.WorkId, message.DispatchSequence, message.EventId, token);
        return new(message.EventId, OperationStatus.Completed, message.WorkId);
    }
}
internal sealed class ClaimPackageWorkCommandHandler(IPackages packages, IUnitOfWork unit, TimeProvider clock)
    : IRequestHandler<ClaimPackageWorkCommand, OperationResult<PackageLease?>>
{
    public async Task<OperationResult<PackageLease?>> Handle(ClaimPackageWorkCommand x, CancellationToken token) =>
        OperationResult<PackageLease?>.Completed(unit.CurrentOperationId!.Value, await packages.ClaimAsync(x.WorkId, x.NodeId, x.LeaseToken, clock.GetUtcNow(), token), x.WorkId);
}
internal sealed class RenewPackageWorkCommandHandler(IPackages packages, IUnitOfWork unit, TimeProvider clock) : IRequestHandler<RenewPackageWorkCommand, OperationResult<bool>>
{
    public async Task<OperationResult<bool>> Handle(RenewPackageWorkCommand x, CancellationToken token)
    { await packages.RenewAsync(x.Lease, clock.GetUtcNow(), token); return OperationResult<bool>.Completed(unit.CurrentOperationId!.Value, true, x.Lease.Work.WorkId); }
}
internal sealed class CompletePackageWorkCommandHandler(IPackages packages, IReleases releases, IUnitOfWork unit, IAuditWriter audit, ICallContext calls)
    : IRequestHandler<CompletePackageWorkCommand, OperationResult<PackageView>>
{
    public async Task<OperationResult<PackageView>> Handle(CompletePackageWorkCommand x, CancellationToken token)
    {
        var view = await packages.CompleteAsync(x.Lease, x.Facts, token);
        await releases.OpenTestAsync(view.ReleaseId, view.Id, token);
        var op = unit.CurrentOperationId!.Value;
        audit.Append(new(op, calls.Current!.Actor.ActorId, "Service", null, null, "pkg.work.completed", view.Id,
            "succeeded", "two independently verified copies", calls.Current.CorrelationId));
        return OperationResult<PackageView>.Completed(op, view, view.Id);
    }
}
internal sealed class FailPackageWorkCommandHandler(IPackages packages, IUnitOfWork unit, IAuditWriter audit, ICallContext calls)
    : IRequestHandler<FailPackageWorkCommand, OperationResult<bool>>
{
    public async Task<OperationResult<bool>> Handle(FailPackageWorkCommand x, CancellationToken token)
    {
        await packages.FailWorkAsync(x.Lease, x.Code, token); var op = unit.CurrentOperationId!.Value;
        audit.Append(new(op, calls.Current!.Actor.ActorId, "Service", null, null, "pkg.work.failed", x.Lease.Work.PackageId, "failed", x.Code, calls.Current.CorrelationId));
        return OperationResult<bool>.Completed(op, true, x.Lease.Work.WorkId);
    }
}
internal sealed class CheckPackageReplicasCommandHandler(IPackages packages, PackageDispatch dispatch, IIntegrationEventOutbox outbox, IUnitOfWork unit)
    : IRequestHandler<CheckPackageReplicasCommand, OperationResult<bool>>
{
    public async Task<OperationResult<bool>> Handle(CheckPackageReplicasCommand x, CancellationToken token)
    {
        var work = await packages.CheckReplicasAsync(x.PackageId, x.Facts, token);
        if (work is not null) await outbox.EnqueueAsync(dispatch.Create(work), token);
        return OperationResult<bool>.Completed(unit.CurrentOperationId!.Value, work is not null, x.PackageId);
    }
}
internal sealed class GetPackageAuthorityQueryHandler(IPackages packages) : IRequestHandler<GetPackageAuthorityQuery, PackageWorkAuthority>
{ public Task<PackageWorkAuthority> Handle(GetPackageAuthorityQuery x, CancellationToken token) => packages.AuthorityAsync(x.WorkId, false, token); }

internal sealed class AuthorizeDownloadCommandHandler(IPackages packages, IPackageDownloadProof proofs, IPersonnelService personnel,
    IInstanceAccess access, IManagedInstances instances, ISoftwareCatalog catalog, IReleases releases, IUnitOfWork unit, IAuditWriter audit, ICallContext calls)
    : IRequestHandler<AuthorizeDownloadCommand, OperationResult<DownloadAuthorization>>
{
    public async Task<OperationResult<DownloadAuthorization>> Handle(AuthorizeDownloadCommand x, CancellationToken token)
    {
        var software = await packages.SoftwareForAsync("package", x.PackageId, false, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        Guid subject; string actor; string? employee = null;
        if (proofs.Instance is { } machine)
        {
            var identity = await access.AuthenticateAsync(machine, true, token) ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid);
            if (identity.Kind != ActorKind.Instance || identity.SoftwareId != software) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
            var instance = await instances.GetIdentityAsync(identity.InstanceId!.Value, true, token);
            if (instance?.Lifecycle != "Active") throw new RequestRejectedException(RequestFailure.InstanceSuspended);
            subject = identity.SubjectId; actor = "Instance";
        }
        else
        {
            var person = await personnel.AuthenticateAsync(proofs.Person ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired), true, token)
                ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
            if (person.MustChangePassword || !person.Permissions.Any(p => p.SoftwareId == software && p.Operation == "software.read")) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
            subject = person.SubjectId; actor = "Human"; employee = person.EmployeeNo;
        }
        if (!await catalog.ExistsAsync(software, true, token)) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var package = await packages.GetAsync(x.PackageId, false, false, token);
        if ((await releases.GetAsync(package.ReleaseId, true, token)).State is not ("Test" or "Formal")) throw new RequestRejectedException(RequestFailure.InvalidState);
        var authorization = await packages.AuthorizeDownloadAsync(x.PackageId, x.RequestId, x.NodeId, x.WorkerGeneration, x.Head, subject, actor, employee, token);
        var op = unit.CurrentOperationId!.Value;
        if (authorization.NewSession) audit.Append(new(op, subject, actor, employee, null, "pkg.download.started", x.RequestId, "authorized", "protected GET", calls.Current!.CorrelationId));
        return OperationResult<DownloadAuthorization>.Completed(op, authorization, x.PackageId);
    }
}
internal sealed class RecordDownloadEndCommandHandler(IPackages packages, IUnitOfWork unit, IAuditWriter audit, ICallContext calls)
    : IRequestHandler<RecordDownloadEndCommand, OperationResult<bool>>
{
    public async Task<OperationResult<bool>> Handle(RecordDownloadEndCommand x, CancellationToken token)
    {
        var recorded = await packages.EndDownloadAsync(x.End, token); var op = unit.CurrentOperationId!.Value;
        if (recorded) audit.Append(new(op, calls.Current!.Actor.ActorId, "Service", null, null, "pkg.download.ended", x.End.RequestId, x.End.Outcome,
            $"bytes={x.End.BytesSent}", calls.Current.CorrelationId));
        return OperationResult<bool>.Completed(op, recorded, x.End.RequestId);
    }
}
