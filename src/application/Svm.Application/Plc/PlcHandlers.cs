using MediatR;
using Svm.Application.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Plc;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Audit;
namespace Svm.Application.Plc;

internal sealed class CreatePlcCredentialCommandHandler(IPlcUploadAccess access, CatalogCompletion completion, PlcAuthorization authorization) : IRequestHandler<CreatePlcCredentialCommand, OperationResult<PlcCredentialView>>
{
    public Task<OperationResult<PlcCredentialView>> Handle(CreatePlcCredentialCommand x,CancellationToken token) => completion.ExecuteAsync("plc.credential.create", "issue scoped upload credential", async actor =>
    {
        foreach (var p in x.Scope.OrderBy(p => p.BindingId)) await authorization.Position(p.BindingId,p.BindingRevision,x.SoftwareId,true,token);
        return await access.CreateAsync(x,actor.SubjectId,token);
    },x=>x.Id,token);
}
internal sealed class RevokePlcCredentialCommandHandler(IPlcUploadAccess access, CatalogCompletion completion) : IRequestHandler<RevokePlcCredentialCommand, OperationResult<PlcCredentialView>>
{
    public Task<OperationResult<PlcCredentialView>> Handle(RevokePlcCredentialCommand x,CancellationToken token) => completion.ExecuteAsync("plc.credential.revoke",x.Reason,()=>access.RevokeAsync(x.CredentialId,x.ExpectedRevision,token),x=>x.Id,token);
}
internal sealed class PublishPlcReleaseCommandHandler(IReleases releases, IPackages packages, IPlcPublication publication, PlcAuthorization authorization, CatalogCompletion completion) : IRequestHandler<PublishPlcReleaseCommand, OperationResult<ReleaseView>>
{
    public Task<OperationResult<ReleaseView>> Handle(PublishPlcReleaseCommand x,CancellationToken token) => completion.ExecuteAsync("plc.release.publish",x.PublishReason,async actor=>
    {
        var r=await releases.GetAsync(x.ReleaseId,true,token);
        var p=await authorization.Position(x.BindingId,x.BindingRevision,r.SoftwareId,true,token);
        await packages.VerifyPublishReadyAsync(r.PackageId,r.Id,r.SoftwareId,token);
        return (await publication.PublishAsync(x,p.HardwareId,actor.SubjectId,actor.EmployeeNo,token)) with { DownloadAvailable=true };
    },x=>x.Id,token);
}
internal sealed class RecordPlcUsageCommandHandler(IReleases releases, IPlcUsage usage, PlcAuthorization authorization, CatalogCompletion completion) : IRequestHandler<RecordPlcUsageCommand, OperationResult<PlcUsageView>>
{
    public Task<OperationResult<PlcUsageView>> Handle(RecordPlcUsageCommand x,CancellationToken token) => completion.ExecuteAsync("plc.usage.record",x.Note,async actor=>
    {
        var r=await releases.GetAsync(x.ReleaseId,true,token);
        if(r.SourceKind!="PLC" || r.State is not ("Test" or "Formal" or "Disabled")) throw new RequestRejectedException(RequestFailure.InvalidState);
        var p=await authorization.Position(x.BindingId,x.BindingRevision,r.SoftwareId,true,token);
        return await usage.RecordAsync(x,p.HardwareId,r.SoftwareId,r.Version,r.State,actor.SubjectId,actor.EmployeeNo,token);
    },x=>x.Id,token);
}
internal sealed class ListPlcCredentialsQueryHandler(IPlcQueries queries) : IRequestHandler<ListPlcCredentialsQuery,PlcPage<PlcCredentialView>>
{ public Task<PlcPage<PlcCredentialView>> Handle(ListPlcCredentialsQuery x,CancellationToken token)=>queries.CredentialsAsync(x.SoftwareId,x.PageSize,x.After,token); }
internal sealed class ListPlcUsageQueryHandler(IPlcQueries queries) : IRequestHandler<ListPlcUsageQuery,PlcPage<PlcUsageView>>
{ public Task<PlcPage<PlcUsageView>> Handle(ListPlcUsageQuery x,CancellationToken token)=>queries.UsageAsync(x.HardwareId,x.PageSize,x.After,token); }
internal sealed class GetPlcUsageQueryHandler(IPlcQueries queries) : IRequestHandler<GetPlcUsageQuery,PlcUsageSummary>
{ public async Task<PlcUsageSummary> Handle(GetPlcUsageQuery x,CancellationToken token)=>(await queries.CurrentAsync([x.BindingId],token)).GetValueOrDefault(x.BindingId) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
internal sealed class GetPlcEvidenceQueryHandler(IPlcPublication publication) : IRequestHandler<GetPlcEvidenceQuery,PlcEvidenceView?>
{ public Task<PlcEvidenceView?> Handle(GetPlcEvidenceQuery x,CancellationToken token)=>publication.GetAsync(x.ReleaseId,token); }

internal sealed class PlcUploadCompletion(IUnitOfWork unit, ICallContext calls, IAuditWriter audit)
{
    internal async Task<OperationResult<T>> ExecuteAsync<T>(string operation,string reason,Func<Task<T>> action,Func<T,Guid> reference,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var value=await action(); var id=unit.CurrentOperationId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var c=calls.Current!; audit.Append(new(id,c.Actor.ActorId,"PlcUpload",null,null,operation,reference(value),"succeeded",reason,c.CorrelationId));
        return OperationResult<T>.Completed(id,value,reference(value));
    }
}
internal sealed class CreatePlcArchiveCommandHandler(IReleases releases,IPackages packages,ICallContext calls,PlcUploadCompletion completion) : IRequestHandler<CreatePlcArchiveCommand,OperationResult<ReleaseUploadResult>>
{
    public Task<OperationResult<ReleaseUploadResult>> Handle(CreatePlcArchiveCommand x,CancellationToken token)=>completion.ExecuteAsync("plc.archive.create",x.ChangeReason,async()=>
    {
        var actor=calls.Current!.Actor; var packageId=Guid.NewGuid();
        var r=await releases.CreateAsync(new(x.Key,actor.SoftwareId!.Value,x.ChangeLevel,x.ChangeSummary,x.ChangeReason,x.ExpectedVersion,x.Package),packageId,actor.ActorId!.Value,token,new(x.BindingId,x.BindingRevision));
        var upload=await packages.CreateAsync(packageId,r.Id,r.SoftwareId,actor.ActorId.Value,x.Package,token);
        return new ReleaseUploadResult(r,upload,$"/api/v1/plc-upload/uploads/{upload:D}/content");
    },x=>x.Release.Id,token);
}
