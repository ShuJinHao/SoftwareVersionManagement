using MediatR;
using FluentValidation;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Plc;
using Svm.Application.Packages;
namespace Svm.Application.Plc;

internal sealed class PlcBeginUploadAdapter(IPackages packages) : PackageAdapter<PlcBeginUploadCommand, UploadReceipt>
{
    public override OperationRequestData Describe(PlcBeginUploadCommand x) => new(x.ReceiveToken, OperationValue.Object(G("uploadId", x.UploadId)), OperationValue.Object(G("receiveToken", x.ReceiveToken), S("nodeId", x.NodeId)));
    public override OperationResultReference GetReference(OperationResult<UploadReceipt> x) => new(x.OperationId, x.Status, x.Value.ReceiveToken);
    public override async Task<OperationResult<UploadReceipt>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<UploadReceipt>.Completed(r.OperationId, await packages.ReceiptAsync(Id(r), token), Id(r));
}
internal sealed class PlcFinishUploadAdapter(IPackages packages) : PackageAdapter<PlcFinishUploadCommand, PackageView>
{
    public override OperationRequestData Describe(PlcFinishUploadCommand x) => new(x.Receipt.ReceiveToken, OperationValue.Object(G("uploadId", x.Receipt.UploadId)),
        OperationValue.Object(G("receiveToken", x.Receipt.ReceiveToken), N("receiveGeneration", x.Receipt.ReceiveGeneration), N("size", x.Size), S("sha256", x.Sha256)));
    public override async Task<OperationResult<PackageView>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<PackageView>.Completed(r.OperationId, await packages.GetAsync(Id(r), false, false, token), Id(r));
}
internal sealed class PlcFailUploadAdapter(IPackages packages) : PackageAdapter<PlcFailUploadCommand, PackageView>
{
    public override OperationRequestData Describe(PlcFailUploadCommand x) => new(x.ReceiveToken, OperationValue.Object(G("uploadId", x.UploadId)), OperationValue.Object(G("receiveToken", x.ReceiveToken), S("code", x.Code)));
    public override async Task<OperationResult<PackageView>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<PackageView>.Completed(r.OperationId, await packages.GetAsync(Id(r), false, false, token), Id(r));
}
internal sealed class PlcRetryPackageAdapter(IPackages packages) : PackageAdapter<PlcRetryPackageCommand, PackageView>
{
    public override OperationRequestData Describe(PlcRetryPackageCommand x) => new(x.Key, OperationValue.Object(G("packageId", x.PackageId)), OperationValue.Object(N("expectedRevision", x.ExpectedRevision), S("reason", x.Reason)));
    public override async Task<OperationResult<PackageView>> RestoreAsync(OperationResultReference r, CancellationToken token) => OperationResult<PackageView>.Completed(r.OperationId, await packages.GetAsync(Id(r), false, false, token), Id(r));
}