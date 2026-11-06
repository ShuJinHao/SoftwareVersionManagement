using Svm.Core.Packages;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.PackageService;

internal sealed partial class Packages
{
    public async Task<PackageCleanupStatus> CleanupStatusAsync(Guid id, bool protect, CancellationToken token)
    {
        var package = await Existing(id, protect, token);
        var works = await repository.WorksAsync(id, protect, token);
        var open = await repository.OpenDownloadsAsync(id, 51, token);
        var blockers = new List<string>();
        if (!package.Disabled) blockers.Add("RELEASE_NOT_DISABLED");
        if (works.Any(w => w.Kind != "Cleanup" && w.State is "Pending" or "Running")) blockers.Add("FILE_WORK_ACTIVE");
        if (open.Count != 0) blockers.Add("DOWNLOADS_OPEN");
        var cleanup = works.SingleOrDefault(w => w.Kind == "Cleanup");
        return new(id, package.Revision, package.State, cleanup?.Id.Value, blockers,
            open.Take(50).Select(g => new OpenDownloadGeneration(g.NodeId, g.WorkerGeneration, g.OpenCount, g.LatestStartedAt)).ToArray(), open.Count > 50);
    }
    public async Task<PackageWorkAuthority?> RequestCleanupAsync(Guid id, long revision, Guid actor, string reason, CancellationToken token)
    {
        RequireWrite(); nodes.Validate(); var package = await Existing(id, true, token);
        if (package.Revision != revision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        var status = await CleanupStatusAsync(id, true, token);
        if (status.BlockingReasons.Count != 0) throw Blocked(status.BlockingReasons);
        if (status.WorkId is { } existing)
        {
            var work = await ExistingWork(existing, true, token);
            if (package.State == "Cleaned" || work.State == "Completed") return null;
            if (work.LeaseUntil > clock.GetUtcNow()) throw Blocked(["CLEANUP_IN_PROGRESS"]);
            // Re-dispatch the same cleanup intent. Fences invalidate old executors; file locks
            // and a current-lease check prevent an earlier process from publishing new bytes.
            work.Dispatch(actor); package.BeginCleanup();
            repository.Add(new PackageDispatch(work.DispatchEventId, work.Id.Value, work.DispatchSequence));
            return Authority(work, package);
        }
        package.BeginCleanup();
        var created = new PackageWork(Guid.NewGuid(), id, package.SoftwareId, actor, clock.GetUtcNow(), "Cleanup");
        created.Dispatch(actor); repository.Add(created);
        repository.Add(new PackageCleanup(created.Id.Value, id, actor, clock.GetUtcNow(), reason));
        repository.Add(new PackageDispatch(created.DispatchEventId, created.Id.Value, created.DispatchSequence));
        return Authority(created, package);
    }
    public async Task<DownloadStopView> RecordStopAsync(Guid id, Guid packageId, Guid actor, DownloadStopInput input, CancellationToken token)
    {
        RequireWrite(); nodes.Require(input.NodeId); var package = await Existing(packageId, true, token);
        if (package.Revision != input.ExpectedRevision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (!package.Disabled || !input.ProcessStoppedConfirmed || input.StoppedAt > clock.GetUtcNow()) throw new RequestRejectedException(RequestFailure.InvalidState);
        var group = (await repository.OpenDownloadsAsync(packageId, 51, token))
            .SingleOrDefault(g => g.NodeId == input.NodeId && g.WorkerGeneration == input.WorkerGeneration);
        if (group is null) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (input.StoppedAt < group.LatestStartedAt) throw new RequestRejectedException(RequestFailure.ValidationFailed);
        var fact = new DownloadStopEvidence(id, packageId, input.NodeId, input.WorkerGeneration, input.StoppedAt,
            actor, clock.GetUtcNow(), input.Evidence, input.Reason);
        repository.Add(fact); package.RecordStopEvidence(); return View(fact);
    }
    public async Task<DownloadStopView> StopEvidenceAsync(Guid id, CancellationToken token) =>
        View(await repository.StopEvidenceAsync(id, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound));
    public async Task RecordCleanupReplicaAsync(PackageLease lease, string node, CancellationToken token)
    {
        RequireWrite(); nodes.Require(node); var (work, package) = await Owned(lease, token);
        if (work.Kind != "Cleanup") throw new RequestRejectedException(RequestFailure.InvalidState);
        var replica = (await repository.ReplicasAsync(package.Id.Value, true, token)).Single(x => x.NodeId == node);
        replica.Check("Deleted", clock.GetUtcNow());
    }
    public async Task<PackageView> CompleteCleanupAsync(PackageLease lease, CancellationToken token)
    {
        RequireWrite(); var (work, package) = await Owned(lease, token);
        if (work.Kind != "Cleanup" || (await repository.OpenDownloadsAsync(package.Id.Value, 1, token)).Count != 0)
            throw new RequestRejectedException(RequestFailure.InvalidState);
        var copies = await repository.ReplicasAsync(package.Id.Value, true, token);
        if (copies.Count != 2 || copies.Any(c => c.State != "Deleted")) throw new RequestRejectedException(RequestFailure.InvalidState);
        package.FinishCleanup(); work.Complete(clock.GetUtcNow()); return await View(package, work, token);
    }
    private static DownloadStopView View(DownloadStopEvidence e) => new(e.Id, e.PackageId, e.NodeId, e.WorkerGeneration,
        e.StoppedAt, e.RecordedBy, e.RecordedAt, e.Evidence, e.Reason);
    private static RequestRejectedException Blocked(IReadOnlyList<string> reasons) =>
        new(RequestFailure.InvalidState, reasons.Select(r => new ValidationIssue("cleanup", r, r)));
}
