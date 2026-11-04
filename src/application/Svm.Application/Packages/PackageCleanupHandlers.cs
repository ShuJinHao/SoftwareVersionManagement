using MediatR;
using Svm.Application.Catalog;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Application.Packages;

internal sealed class RequestPackageCleanupHandler(IPackages packages, IReleases releases, IReleaseTaskReferences tasks,
    CatalogCompletion completion, ICallContext calls, PackageDispatch dispatch, IIntegrationEventOutbox outbox)
    : IRequestHandler<RequestPackageCleanupCommand, OperationResult<PackageWorkView>>
{
    public Task<OperationResult<PackageWorkView>> Handle(RequestPackageCleanupCommand x, CancellationToken ct) => completion.ExecuteAsync(
        "pkg.cleanup.requested", x.Reason, async () =>
        {
            var package = await packages.GetAsync(x.PackageId, false, false, ct);
            if ((await releases.GetAsync(package.ReleaseId, true, ct)).State != "Disabled") throw Block("RELEASE_NOT_DISABLED");
            if (await tasks.HasUnfinishedAsync(package.ReleaseId, ct)) throw Block("TASKS_UNFINISHED");
            var work = await packages.RequestCleanupAsync(x.PackageId, x.ExpectedRevision, calls.Current!.Actor.ActorId!.Value, x.Reason, ct);
            if (work is not null) await outbox.EnqueueAsync(dispatch.Create(work), ct);
            var id = work?.WorkId ?? (await packages.CleanupStatusAsync(x.PackageId, false, ct)).WorkId!.Value;
            return await packages.WorkAsync(id, ct);
        }, work => work.Id, ct);
    private static RequestRejectedException Block(string reason) => new(RequestFailure.InvalidState, [new("cleanup", reason, reason)]);
}
internal sealed class PackageCleanupStatusHandler(IPackageQueries queries)
    : IRequestHandler<GetPackageCleanupStatusQuery, PackageCleanupStatus>
{
    public async Task<PackageCleanupStatus> Handle(GetPackageCleanupStatusQuery x, CancellationToken ct)
        => await queries.CleanupStatusAsync(x.PackageId, ct) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
}
internal sealed class RecordDownloadStopHandler(IPackages packages, CatalogCompletion completion, ICallContext calls)
    : IRequestHandler<RecordDownloadStopCommand, OperationResult<DownloadStopView>>
{
    public Task<OperationResult<DownloadStopView>> Handle(RecordDownloadStopCommand x, CancellationToken ct) => completion.ExecuteAsync(
        "pkg.download.process-stop-confirmed", x.Input.Reason,
        () => packages.RecordStopAsync(Guid.NewGuid(), x.PackageId, calls.Current!.Actor.ActorId!.Value, x.Input, ct), value => value.Id, ct);
}
internal sealed class RecordCleanupReplicaHandler(IPackages packages, IUnitOfWork unit, IAuditWriter audit, ICallContext calls)
    : IRequestHandler<RecordCleanupReplicaCommand, OperationResult<bool>>
{
    public async Task<OperationResult<bool>> Handle(RecordCleanupReplicaCommand x, CancellationToken ct)
    {
        await packages.RecordCleanupReplicaAsync(x.Lease, x.NodeId, ct); var operation = unit.CurrentOperationId!.Value;
        audit.Append(new(operation, calls.Current!.Actor.ActorId, "Service", null, null, "pkg.cleanup.replica-removed", x.Lease.Work.PackageId, "succeeded", x.NodeId, calls.Current.CorrelationId));
        return OperationResult<bool>.Completed(operation, true, x.Lease.Work.WorkId);
    }
}
internal sealed class CompletePackageCleanupHandler(IPackages packages, IUnitOfWork unit, IAuditWriter audit, ICallContext calls)
    : IRequestHandler<CompletePackageCleanupCommand, OperationResult<PackageView>>
{
    public async Task<OperationResult<PackageView>> Handle(CompletePackageCleanupCommand x, CancellationToken ct)
    {
        var result = await packages.CompleteCleanupAsync(x.Lease, ct); var operation = unit.CurrentOperationId!.Value;
        audit.Append(new(operation, calls.Current!.Actor.ActorId, "Service", null, null, "pkg.cleanup.completed", result.Id,
            "succeeded", "both nodes confirmed absent; history retained", calls.Current.CorrelationId));
        return OperationResult<PackageView>.Completed(operation, result, result.Id);
    }
}
