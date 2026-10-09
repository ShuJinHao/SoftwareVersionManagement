using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Application.Packages;

internal abstract class PackageAdapter<T, V> : IIdempotencyRequestAdapter<T, OperationResult<V>> where T : notnull
{
    public abstract OperationRequestData Describe(T x);
    public virtual OperationResultReference GetReference(OperationResult<V> x) => new(x.OperationId, x.Status, x.ResourceId);
    public abstract Task<OperationResult<V>> RestoreAsync(OperationResultReference reference, CancellationToken token);
    protected static Guid Id(OperationResultReference r) => r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    protected static OperationField G(string n, Guid x) => new(n, OperationValue.Identifier(x));
    protected static OperationField S(string n, string? x) => new(n, x is null ? OperationValue.Null : OperationValue.Text(x));
    protected static OperationField N(string n, long x) => new(n, OperationValue.Integer(x));
}
internal sealed class CreateReleaseAdapter(IReleases releases, IPackages packages) : PackageAdapter<CreateReleaseCommand, ReleaseUploadResult>
{
    public override OperationRequestData Describe(CreateReleaseCommand x) => new(x.Key, OperationValue.Object(G("softwareId", x.SoftwareId)),
        OperationValue.Object(S("changeLevel", x.ChangeLevel), S("changeSummary", x.ChangeSummary), S("changeReason", x.ChangeReason),
            S("expectedVersion", x.ExpectedVersion), S("fileName", x.Package.FileName), N("sizeBytes", x.Package.SizeBytes), S("sha256", x.Package.Sha256.ToLowerInvariant())));
    public override async Task<OperationResult<ReleaseUploadResult>> RestoreAsync(OperationResultReference r, CancellationToken token)
    {
        var release = await releases.GetAsync(Id(r), false, token);
        release = release with { DownloadAvailable = (await packages.GetAsync(release.PackageId, false, false, token)).DownloadAvailable }; var upload = await packages.UploadIdAsync(release.PackageId, token);
        return OperationResult<ReleaseUploadResult>.Completed(r.OperationId, new(release, upload, $"/api/v1/manage/uploads/{upload:D}/content"), release.Id);
    }
}
internal sealed class DisableReleaseAdapter(IReleases releases) : PackageAdapter<DisableReleaseCommand, ReleaseView>
{
    public override OperationRequestData Describe(DisableReleaseCommand x) => new(x.Key, OperationValue.Object(G("releaseId", x.ReleaseId)), OperationValue.Object(N("expectedRevision", x.ExpectedRevision), S("reason", x.Reason)));
    public override async Task<OperationResult<ReleaseView>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<ReleaseView>.Completed(r.OperationId, await releases.GetAsync(Id(r), false, token), Id(r));
}
internal sealed class PublishReleaseAdapter(IReleases releases, IPackages packages) : PackageAdapter<PublishReleaseCommand, ReleaseView>
{
    public override OperationRequestData Describe(PublishReleaseCommand x) => new(x.Key, OperationValue.Object(G("releaseId", x.ReleaseId)),
        OperationValue.Object(N("expectedRevision", x.ExpectedRevision), G("testEvidenceId", x.TestEvidenceId), S("publishReason", x.PublishReason), S("publishConclusion", x.PublishConclusion)));
    public override async Task<OperationResult<ReleaseView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        var release = await releases.GetAsync(Id(reference), false, token);
        var available = (await packages.GetAsync(release.PackageId, false, false, token)).DownloadAvailable;
        return OperationResult<ReleaseView>.Completed(reference.OperationId, release with { DownloadAvailable = available }, release.Id);
    }
}
internal sealed class RetryPackageAdapter(IPackages packages) : PackageAdapter<RetryPackageCommand, PackageView>
{
    public override OperationRequestData Describe(RetryPackageCommand x) => new(x.Key, OperationValue.Object(G("packageId", x.PackageId)), OperationValue.Object(N("expectedRevision", x.ExpectedRevision), S("reason", x.Reason)));
    public override async Task<OperationResult<PackageView>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<PackageView>.Completed(r.OperationId, await packages.GetAsync(Id(r), false, false, token), Id(r));
}
internal sealed class BeginUploadAdapter(IPackages packages) : PackageAdapter<BeginUploadCommand, UploadReceipt>
{
    public override OperationRequestData Describe(BeginUploadCommand x) => new(x.ReceiveToken, OperationValue.Object(G("uploadId", x.UploadId)), OperationValue.Object(G("receiveToken", x.ReceiveToken), S("nodeId", x.NodeId)));
    public override OperationResultReference GetReference(OperationResult<UploadReceipt> x) => new(x.OperationId, x.Status, x.Value.ReceiveToken);
    public override async Task<OperationResult<UploadReceipt>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<UploadReceipt>.Completed(r.OperationId, await packages.ReceiptAsync(Id(r), token), Id(r));
}
internal sealed class FinishUploadAdapter(IPackages packages) : PackageAdapter<FinishUploadCommand, PackageView>
{
    public override OperationRequestData Describe(FinishUploadCommand x) => new(x.Receipt.ReceiveToken, OperationValue.Object(G("uploadId", x.Receipt.UploadId)),
        OperationValue.Object(G("receiveToken", x.Receipt.ReceiveToken), N("receiveGeneration", x.Receipt.ReceiveGeneration), N("size", x.Size), S("sha256", x.Sha256)));
    public override async Task<OperationResult<PackageView>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<PackageView>.Completed(r.OperationId, await packages.GetAsync(Id(r), false, false, token), Id(r));
}
internal sealed class FailUploadAdapter(IPackages packages) : PackageAdapter<FailUploadCommand, PackageView>
{
    public override OperationRequestData Describe(FailUploadCommand x) => new(x.ReceiveToken, OperationValue.Object(G("uploadId", x.UploadId)), OperationValue.Object(G("receiveToken", x.ReceiveToken), S("code", x.Code)));
    public override async Task<OperationResult<PackageView>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<PackageView>.Completed(r.OperationId, await packages.GetAsync(Id(r), false, false, token), Id(r));
}

internal sealed class DownloadEndAdapter : PackageAdapter<RecordDownloadEndCommand,bool>
{
    public override OperationRequestData Describe(RecordDownloadEndCommand x) => new(x.End.RequestId,OperationValue.Object(G("requestId",x.End.RequestId)),
        OperationValue.Object(S("nodeId",x.End.NodeId),G("workerGeneration",x.End.WorkerGeneration),new("endedAt",OperationValue.Timestamp(x.End.EndedAt)),N("bytesSent",x.End.BytesSent),S("outcome",x.End.Outcome)));
    public override Task<OperationResult<bool>> RestoreAsync(OperationResultReference r,CancellationToken t) => Task.FromResult(OperationResult<bool>.Completed(r.OperationId,true,Id(r)));
}
