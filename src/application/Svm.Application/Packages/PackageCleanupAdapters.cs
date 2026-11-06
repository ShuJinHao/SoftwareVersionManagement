using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Application.Packages;

internal sealed class RequestPackageCleanupAdapter(IPackages packages) : PackageAdapter<RequestPackageCleanupCommand, PackageWorkView>
{
    public override OperationRequestData Describe(RequestPackageCleanupCommand x) => new(x.Key,
        OperationValue.Object(G("packageId", x.PackageId)), OperationValue.Object(N("expectedRevision", x.ExpectedRevision), S("reason", x.Reason)));
    public override async Task<OperationResult<PackageWorkView>> RestoreAsync(OperationResultReference r, CancellationToken ct) =>
        OperationResult<PackageWorkView>.Completed(r.OperationId, await packages.WorkAsync(Id(r), ct), Id(r));
}
internal sealed class RecordDownloadStopAdapter(IPackages packages) : PackageAdapter<RecordDownloadStopCommand, DownloadStopView>
{
    public override OperationRequestData Describe(RecordDownloadStopCommand x) => new(x.Key, OperationValue.Object(G("packageId", x.PackageId)),
        OperationValue.Object(S("nodeId", x.Input.NodeId), G("workerGeneration", x.Input.WorkerGeneration),
            new("stoppedAt", OperationValue.Timestamp(x.Input.StoppedAt)), new("processStoppedConfirmed", OperationValue.Boolean(x.Input.ProcessStoppedConfirmed)),
            S("evidence", x.Input.Evidence), S("reason", x.Input.Reason), N("expectedRevision", x.Input.ExpectedRevision)));
    public override async Task<OperationResult<DownloadStopView>> RestoreAsync(OperationResultReference r, CancellationToken ct) =>
        OperationResult<DownloadStopView>.Completed(r.OperationId, await packages.StopEvidenceAsync(Id(r), ct), Id(r));
}
