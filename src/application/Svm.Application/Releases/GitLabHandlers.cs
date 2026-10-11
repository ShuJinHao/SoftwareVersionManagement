using MediatR;
using Svm.Application.Catalog;
using Svm.Application.Packages;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;

namespace Svm.Application.Releases;

internal sealed class RequestReleaseSyncCommandHandler(IReleaseImports imports, CatalogCompletion completion) : IRequestHandler<RequestReleaseSyncCommand, OperationResult<ReleaseSyncWorkView>>
{
    public Task<OperationResult<ReleaseSyncWorkView>> Handle(RequestReleaseSyncCommand x, CancellationToken token) =>
        completion.ExecuteAsync("rel.sync.requested", "explicit GitLab synchronization", () => imports.ScheduleAsync(x.SoftwareId, x.Tag, false, token), w => w.Id, token);
}
internal sealed class RequestReleaseSyncAdapter(IReleaseImports imports) : PackageAdapter<RequestReleaseSyncCommand, ReleaseSyncWorkView>
{
    public override OperationRequestData Describe(RequestReleaseSyncCommand x) => new(x.Key, OperationValue.Object(G("softwareId", x.SoftwareId)), OperationValue.Object(S("tag", x.Tag)));
    public override async Task<OperationResult<ReleaseSyncWorkView>> RestoreAsync(OperationResultReference r, CancellationToken token) =>
        OperationResult<ReleaseSyncWorkView>.Completed(r.OperationId, await imports.ViewAsync(Id(r), token), Id(r));
}
internal sealed class GetReleaseSourceQueryHandler(IReleaseImportQueries imports) : IRequestHandler<GetReleaseSourceQuery, ReleaseSourceView>
{ public Task<ReleaseSourceView> Handle(GetReleaseSourceQuery x, CancellationToken token) => imports.SourceAsync(x.SoftwareId, token); }
internal sealed class GetReleaseSyncWorkQueryHandler(IReleaseImportQueries imports) : IRequestHandler<GetReleaseSyncWorkQuery, ReleaseSyncWorkView>
{ public Task<ReleaseSyncWorkView> Handle(GetReleaseSyncWorkQuery x, CancellationToken token) => imports.WorkAsync(x.WorkId, token); }
internal sealed class GetGitLabWorkQueryHandler(IReleaseImports imports) : IRequestHandler<GetGitLabWorkQuery, GitLabWork>
{ public Task<GitLabWork> Handle(GetGitLabWorkQuery x, CancellationToken token) => imports.WorkAsync(x.WorkId, false, token); }
internal sealed class GetGitLabSourceQueryHandler(IReleaseImports imports) : IRequestHandler<GetGitLabSourceQuery, ReleaseSourceView>
{ public Task<ReleaseSourceView> Handle(GetGitLabSourceQuery x, CancellationToken token) => imports.SourceAsync(x.SoftwareId, token); }
internal sealed class ScheduleGitLabScanCommandHandler(IReleaseImports imports, IUnitOfWork unit) : IRequestHandler<ScheduleGitLabScanCommand, OperationResult<ReleaseSyncWorkView>>
{ public async Task<OperationResult<ReleaseSyncWorkView>> Handle(ScheduleGitLabScanCommand x, CancellationToken token) { var w = await imports.ScheduleAsync(x.SoftwareId, null, true, token); return OperationResult<ReleaseSyncWorkView>.Completed(unit.CurrentOperationId!.Value, w, w.Id); } }
internal sealed class ClaimGitLabWorkCommandHandler(IReleaseImports imports, IUnitOfWork unit) : IRequestHandler<ClaimGitLabWorkCommand, OperationResult<GitLabWork?>>
{ public async Task<OperationResult<GitLabWork?>> Handle(ClaimGitLabWorkCommand x, CancellationToken token) => OperationResult<GitLabWork?>.Completed(unit.CurrentOperationId!.Value, await imports.ClaimAsync(x.WorkId, x.LeaseToken, token), x.WorkId); }
internal sealed class ApplyGitLabScanCommandHandler(IReleaseImports imports, IUnitOfWork unit) : IRequestHandler<ApplyGitLabScanCommand, OperationResult<bool>>
{ public async Task<OperationResult<bool>> Handle(ApplyGitLabScanCommand x, CancellationToken token) { await imports.ScanAsync(x.Lease, x.Releases, x.Tags, x.NextPage, token); return OperationResult<bool>.Completed(unit.CurrentOperationId!.Value, true, x.Lease.WorkId); } }
internal sealed class RenewGitLabWorkCommandHandler(IReleaseImports imports, IUnitOfWork unit) : IRequestHandler<RenewGitLabWorkCommand, OperationResult<bool>>
{ public async Task<OperationResult<bool>> Handle(RenewGitLabWorkCommand x, CancellationToken token) { await imports.ProtectAsync(x.Lease, token); return OperationResult<bool>.Completed(unit.CurrentOperationId!.Value, true, x.Lease.WorkId); } }
internal sealed class DelayGitLabWorkCommandHandler(IReleaseImports imports, IUnitOfWork unit) : IRequestHandler<DelayGitLabWorkCommand, OperationResult<bool>>
{ public async Task<OperationResult<bool>> Handle(DelayGitLabWorkCommand x, CancellationToken token) { await imports.DelayAsync(x.Lease, x.Code, token); return OperationResult<bool>.Completed(unit.CurrentOperationId!.Value, true, x.Lease.WorkId); } }
internal sealed class BeginGitLabImportCommandHandler(IReleaseImports imports, IPackages packages, GitLabImportOptions options, IUnitOfWork unit, IAuditWriter audit, ICallContext calls, PackageDispatch dispatch, IIntegrationEventOutbox outbox)
    : IRequestHandler<BeginGitLabImportCommand, OperationResult<GitLabImportReceipt>>
{
    public async Task<OperationResult<GitLabImportReceipt>> Handle(BeginGitLabImportCommand x, CancellationToken token)
    {
        var before = await imports.ProtectAsync(x.Lease, token); var newPackageId = Guid.NewGuid();
        var release = await imports.RegisterAsync(x.Lease, x.Facts, newPackageId, token);
        if (release.PackageId == newPackageId)
        {
            var m = x.Facts.Manifest;
            await packages.CreateAsync(newPackageId, release.Id, release.SoftwareId, options.Require(release.SoftwareId).SubjectId, new(m.FileName, m.SizeBytes, m.Sha256), token);
            audit.Append(new(unit.CurrentOperationId!.Value, calls.Current!.Actor.ActorId, "Service", null, null, "rel.gitlab.registered", release.Id, "succeeded", "verified protected tag and build", calls.Current.CorrelationId));
        }
        var p = await packages.GetAsync(release.PackageId, false, true, token); UploadReceipt? upload = null;
        if (release.State is "Formal" or "Disabled")
        {
            if (release.State == "Formal") await imports.CompleteReleaseAsync(release.Id, release.PackageId, token);
            else await imports.DelayAsync(x.Lease, "VERSION_CONFLICT", token);
        }
        else if (p.State is "Ready" or "Verifying" or "Copying") await imports.ReceivedAsync(x.Lease, token);
        else if (p.State == "Failed" && p.SizeBytes is not null)
        {
            var sending = await packages.RetryAsync(p.Id, p.Revision, options.Require(release.SoftwareId).SubjectId, token);
            await outbox.EnqueueAsync(dispatch.Create(sending), token); await imports.ReceivedAsync(x.Lease, token);
        }
        else
        {
            // Interrupted downloads use a fresh receive fence. The node lock is held outside this transaction.
            if (before.ReceiveToken is { } previous) await packages.FailUploadAsync(p.UploadId, previous, "IMPORT_INTERRUPTED", token);
            upload = await packages.BeginImportedAsync(p.UploadId, x.ReceiveToken, x.NodeId, token);
            await imports.SetReceiveAsync(x.Lease, x.ReceiveToken, token);
        }
        return OperationResult<GitLabImportReceipt>.Completed(unit.CurrentOperationId!.Value, new(await imports.WorkAsync(x.Lease.WorkId, true, token), upload), release.Id);
    }
}
internal sealed class FinishGitLabImportCommandHandler(IReleaseImports imports, IPackages packages, PackageDispatch dispatch,
    IIntegrationEventOutbox outbox, IUnitOfWork unit, IAuditWriter audit, ICallContext calls) : IRequestHandler<FinishGitLabImportCommand, OperationResult<bool>>
{
    public async Task<OperationResult<bool>> Handle(FinishGitLabImportCommand x, CancellationToken token)
    {
        var work = await imports.ProtectAsync(x.Lease, token);
        if (work.ReceiveToken != x.Receipt.ReceiveToken || work.ReleaseId != x.Receipt.ReleaseId) throw new RequestRejectedException(RequestFailure.InvalidState);
        var sending = await packages.FinishAsync(x.Receipt, x.Size, x.Sha256, token);
        await outbox.EnqueueAsync(dispatch.Create(sending), token);
        await imports.ReceivedAsync(x.Lease, token);
        audit.Append(new(unit.CurrentOperationId!.Value, calls.Current!.Actor.ActorId, "Service", null, null, "pkg.gitlab.received", x.Receipt.PackageId, "succeeded", "size and SHA-256 verified", calls.Current.CorrelationId));
        return OperationResult<bool>.Completed(unit.CurrentOperationId!.Value, true, work.Id);
    }
}
