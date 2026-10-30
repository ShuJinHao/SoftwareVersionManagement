using MediatR;
using FluentValidation;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Plc;
using Svm.Application.Packages;
namespace Svm.Application.Plc;

internal sealed class PlcUploadContentCommandHandler(IPackageFiles files, ICommandScopeExecutor phases, IPackages packages)
    : IRequestHandler<PlcUploadContentCommand, OperationResult<PackageView>>
{
    public async Task<OperationResult<PackageView>> Handle(PlcUploadContentCommand x, CancellationToken token)
    {
        await using var mutex = await files.LockUploadAsync(x.UploadId, token);
        var receiveToken = Guid.NewGuid();
        var begin = await phases.ExecuteAsync(new PlcBeginUploadCommand(x.UploadId, receiveToken, files.NodeId), token);
        if (begin.Value.AlreadyReceived) return OperationResult<PackageView>.Completed(begin.OperationId, await packages.GetAsync(x.UploadId, true, false, token), begin.Value.PackageId);
        (long Size, string Sha256) received;
        try
        {
            received = await files.ReceiveAsync(begin.Value, x.Content, x.ContentLength,
                async ct => { await phases.QueryAsync(new PlcCheckUploadQuery(x.UploadId, receiveToken), ct); }, token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or RequestRejectedException)
        {
            // A separate cancellation budget can record an interrupted receive. It cannot grant new authority.
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await phases.ExecuteAsync(new PlcFailUploadCommand(x.UploadId, receiveToken,
                error is RequestRejectedException rejected ? rejected.Code : "UPLOAD_INTERRUPTED"), budget.Token); }
            catch (Exception failure) when (failure is RequestRejectedException or PersistenceException or OperationCanceledException) { }
            throw;
        }
        // Do not turn a commit-unknown result into a new receive or another sending intent.
        return await phases.ExecuteAsync(new PlcFinishUploadCommand(begin.Value, received.Size, received.Sha256), token);
    }
}
internal sealed class PlcBeginUploadCommandHandler(IPackages packages, IUnitOfWork unit) : IRequestHandler<PlcBeginUploadCommand, OperationResult<UploadReceipt>>
{
    public async Task<OperationResult<UploadReceipt>> Handle(PlcBeginUploadCommand x, CancellationToken token)
    { var r = await packages.BeginAsync(x.UploadId, x.ReceiveToken, x.NodeId, token); return OperationResult<UploadReceipt>.Completed(unit.CurrentOperationId!.Value, r, r.PackageId); }
}
internal sealed class PlcFinishUploadCommandHandler(IPackages packages, PackageDispatch dispatch, IIntegrationEventOutbox outbox, PlcUploadCompletion completion)
    : IRequestHandler<PlcFinishUploadCommand, OperationResult<PackageView>>
{
    public Task<OperationResult<PackageView>> Handle(PlcFinishUploadCommand x, CancellationToken token) => completion.ExecuteAsync(
        "pkg.upload.received", "size and SHA-256 verified", async () => { var w = await packages.FinishAsync(x.Receipt, x.Size, x.Sha256, token); await outbox.EnqueueAsync(dispatch.Create(w), token); return await packages.GetAsync(w.PackageId, false, false, token); }, x => x.Id, token);
}
internal sealed class PlcFailUploadCommandHandler(IPackages packages, PlcUploadCompletion completion) : IRequestHandler<PlcFailUploadCommand, OperationResult<PackageView>>
{
    public Task<OperationResult<PackageView>> Handle(PlcFailUploadCommand x, CancellationToken token) => completion.ExecuteAsync("pkg.upload.failed", x.Code,
        async () => { await packages.FailUploadAsync(x.UploadId, x.ReceiveToken, x.Code, token); return await packages.GetAsync(x.UploadId, true, false, token); }, x => x.Id, token);
}
internal sealed class PlcCheckUploadQueryHandler(IPackages packages) : IRequestHandler<PlcCheckUploadQuery, UploadReceipt>
{
    public async Task<UploadReceipt> Handle(PlcCheckUploadQuery x, CancellationToken token)
    { var r = await packages.ReceivingAsync(x.UploadId, x.ReceiveToken, token); if (r.AlreadyReceived) throw new RequestRejectedException(RequestFailure.InvalidState); return r; }
}
internal sealed class PlcRetryPackageCommandHandler(IPackages packages, ICallContext calls, PackageDispatch dispatch, IIntegrationEventOutbox outbox, PlcUploadCompletion completion)
    : IRequestHandler<PlcRetryPackageCommand, OperationResult<PackageView>>
{
    public Task<OperationResult<PackageView>> Handle(PlcRetryPackageCommand x, CancellationToken token) => completion.ExecuteAsync(
        "pkg.package.retry", x.Reason, async () => { var w = await packages.RetryAsync(x.PackageId, x.ExpectedRevision, calls.Current!.Actor.ActorId!.Value, token); await outbox.EnqueueAsync(dispatch.Create(w), token); return await packages.GetAsync(x.PackageId, false, false, token); }, x => x.Id, token);
}
internal sealed class PlcGetPackageQueryHandler(IPackages packages) : IRequestHandler<PlcGetPackageQuery, PackageView>
{ public async Task<PackageView> Handle(PlcGetPackageQuery x, CancellationToken t) => await packages.GetAsync(x.PackageId, x.Upload, false, t); }
internal sealed class PlcGetPackageWorkQueryHandler(IPackages packages) : IRequestHandler<PlcGetPackageWorkQuery, PackageWorkView>
{ public async Task<PackageWorkView> Handle(PlcGetPackageWorkQuery x, CancellationToken t) => await packages.WorkAsync(x.WorkId, t); }
internal sealed class PlcGetUploadTargetQueryHandler(IPackages packages) : IRequestHandler<PlcGetUploadTargetQuery,string>
{ public async Task<string> Handle(PlcGetUploadTargetQuery x,CancellationToken t) => (await packages.AuthorityAsync(x.UploadId,false,t)).SourceNode; }
