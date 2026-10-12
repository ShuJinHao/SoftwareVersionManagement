using MediatR;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;

namespace Svm.Application.Instances;

internal sealed class InstanceCompletion(ICallContext calls,IUnitOfWork unitOfWork,IAuditWriter audit,IPersonnelService personnel,ISessionProofSource proof)
{
    internal async Task<OperationResult<T>> CompleteAsync<T>(T value,Guid resource,string operation,string reason,CancellationToken token,bool record=true)
    {
        var call=calls.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var id=unitOfWork.CurrentOperationId ?? throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        PersonnelView? human=null;
        if(call.Actor.Kind==ActorKind.Human) human=await personnel.AuthenticateAsync(proof.Proof!,true,token) ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        if(record) audit.Append(new(id,call.Actor.ActorId,call.Actor.Kind.ToString(),human?.EmployeeNo,human?.DisplayName,operation,resource,"succeeded",reason,call.CorrelationId));
        return OperationResult<T>.Completed(id,value,resource);
    }
}
internal sealed class CreateEnrollmentGrantCommandHandler(IInstanceAccess access,ISiteAssets assets,InstanceCompletion completion) : IRequestHandler<CreateEnrollmentGrantCommand,OperationResult<GrantView>>
{
    public async Task<OperationResult<GrantView>> Handle(CreateEnrollmentGrantCommand x,CancellationToken token)
    {
        // Validate every fixed device against the active mapping without permanently marking a reference before registration.
        foreach(var id in x.DeviceIds.Order()) { await assets.DeviceAsync(id,true,token); if(!await assets.HasActiveBindingAsync(id,x.SoftwareId,token)) throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
        var v=await access.CreateEnrollmentAsync(x,token); return await completion.CompleteAsync(v,v.Id,"iam.enrollment.create",x.Reason,token);
    }
}
internal sealed class RevokeEnrollmentGrantCommandHandler(IInstanceAccess access,InstanceCompletion c) : IRequestHandler<RevokeEnrollmentGrantCommand,OperationResult<GrantView>>
{ public async Task<OperationResult<GrantView>> Handle(RevokeEnrollmentGrantCommand x,CancellationToken t) { var v=await access.RevokeEnrollmentAsync(x.GrantId,x.ExpectedRevision,t); return await c.CompleteAsync(v,v.Id,"iam.enrollment.revoke",x.Reason,t); } }
internal sealed class CreateRecoveryGrantCommandHandler(IInstanceAccess access,InstanceCompletion c) : IRequestHandler<CreateRecoveryGrantCommand,OperationResult<GrantView>>
{ public async Task<OperationResult<GrantView>> Handle(CreateRecoveryGrantCommand x,CancellationToken t) { var v=await access.CreateRecoveryAsync(x,t); return await c.CompleteAsync(v,v.Id,"iam.recovery.create",x.Reason,t); } }
internal sealed class RevokeRecoveryGrantCommandHandler(IInstanceAccess access,InstanceCompletion c) : IRequestHandler<RevokeRecoveryGrantCommand,OperationResult<GrantView>>
{ public async Task<OperationResult<GrantView>> Handle(RevokeRecoveryGrantCommand x,CancellationToken t) { var v=await access.RevokeRecoveryAsync(x.GrantId,x.ExpectedRevision,t); return await c.CompleteAsync(v,v.Id,"iam.recovery.revoke",x.Reason,t); } }
internal sealed class RevokeInstanceCredentialCommandHandler(IInstanceAccess access,InstanceCompletion c) : IRequestHandler<RevokeInstanceCredentialCommand,OperationResult<CredentialView>>
{ public async Task<OperationResult<CredentialView>> Handle(RevokeInstanceCredentialCommand x,CancellationToken t) { var v=await access.RevokeCredentialAsync(x.CredentialId,x.ExpectedRevision,t); return await c.CompleteAsync(v,v.Id,"iam.instance-credentials.revoke",x.Reason,t); } }
internal sealed class UpdateInstanceLifecycleCommandHandler(IManagedInstances instances,InstanceCompletion c) : IRequestHandler<UpdateInstanceLifecycleCommand,OperationResult<InstanceIdentity>>
{ public async Task<OperationResult<InstanceIdentity>> Handle(UpdateInstanceLifecycleCommand x,CancellationToken t) { var v=await instances.LifecycleAsync(x.InstanceId,x.ExpectedRevision,x.Lifecycle,t); return await c.CompleteAsync(v,v.Id,"ins.lifecycle.update",x.Reason,t); } }
internal sealed class RegisterInstanceCommandHandler(IInstanceAccess access,IManagedInstances instances,ISiteAssets assets,ISoftwareCatalog software,InstanceCompletion c) : IRequestHandler<RegisterInstanceCommand,OperationResult<RegistrationResult>>
{
    public async Task<OperationResult<RegistrationResult>> Handle(RegisterInstanceCommand x,CancellationToken t)
    {
        if(!await software.ExistsAsync(x.SoftwareId,true,t)) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        await assets.ConfirmInstanceReferenceAsync(x.DeviceId,x.SoftwareId,t);
        var id=Guid.NewGuid(); await instances.CreateAsync(id,x.SoftwareId,x.DeviceId,x.InstallationKey,t);
        var v=await access.RegisterAsync(x,id,t); return await c.CompleteAsync(v,v.InstanceId,"iam.instances.register","enrollment grant registration",t);
    }
}
internal sealed class RecoverInstanceCommandHandler(IInstanceAccess access,IManagedInstances instances,ICallContext calls,InstanceCompletion c) : IRequestHandler<RecoverInstanceCommand,OperationResult<RegistrationResult>>
{
    public async Task<OperationResult<RegistrationResult>> Handle(RecoverInstanceCommand x,CancellationToken t)
    {
        var id=calls.Current!.Actor.InstanceId!.Value; var epoch=await instances.InvalidateStreamAsync(id,t);
        var v=await access.RecoverAsync(x,epoch,t); return await c.CompleteAsync(v,v.InstanceId,"iam.instances.recover","single-use instance recovery",t);
    }
}
internal sealed class OpenReportStreamCommandHandler(IManagedInstances instances,ICallContext calls,InstanceCompletion c) : IRequestHandler<OpenReportStreamCommand,OperationResult<StreamResult>>
{ public async Task<OperationResult<StreamResult>> Handle(OpenReportStreamCommand x,CancellationToken t) { var id=calls.Current!.Actor.InstanceId!.Value; var v=await instances.OpenStreamAsync(id,x.ExpectedEpoch,t); return await c.CompleteAsync(v,id,"ins.report-streams.open","open report stream",t); } }
internal sealed class SubmitStatusReportCommandHandler(IManagedInstances instances,ICallContext calls,InstanceCompletion c) : IRequestHandler<SubmitStatusReportCommand,OperationResult<ReportResult>>
{ public async Task<OperationResult<ReportResult>> Handle(SubmitStatusReportCommand x,CancellationToken t) { var id=calls.Current!.Actor.InstanceId!.Value; var v=await instances.ReportAsync(id,x.Report,t); return await c.CompleteAsync(v,id,"ins.status-reports.accept","installation evidence received",t,record:v.Applied && v.EvidenceId is not null); } }
internal sealed class ListEnrollmentGrantsQueryHandler(IInstanceQueries queries) : IRequestHandler<ListEnrollmentGrantsQuery,InstancePage<GrantView>>
{ public Task<InstancePage<GrantView>> Handle(ListEnrollmentGrantsQuery x,CancellationToken t) => queries.GrantsAsync(x.SoftwareId,x.PageSize,x.After,t); }
internal sealed class ListInstanceCredentialsQueryHandler(IInstanceQueries queries) : IRequestHandler<ListInstanceCredentialsQuery,InstancePage<CredentialView>>
{ public Task<InstancePage<CredentialView>> Handle(ListInstanceCredentialsQuery x,CancellationToken t) => queries.CredentialsAsync(x.InstanceId,x.PageSize,x.After,t); }
internal sealed class ListInstancesQueryHandler(IInstanceQueries queries) : IRequestHandler<ListInstancesQuery,InstancePage<InstanceView>>
{ public Task<InstancePage<InstanceView>> Handle(ListInstancesQuery x,CancellationToken t) => queries.ListAsync(x.Input,t); }
internal sealed class GetInstanceQueryHandler(IInstanceQueries queries) : IRequestHandler<GetInstanceQuery,InstanceView>
{ public async Task<InstanceView> Handle(GetInstanceQuery x,CancellationToken t) => await queries.GetAsync(x.InstanceId,t) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
internal sealed class GetInstanceHistoryQueryHandler(IInstanceQueries queries) : IRequestHandler<GetInstanceHistoryQuery,InstancePage<InstallationHistoryView>>
{ public Task<InstancePage<InstallationHistoryView>> Handle(GetInstanceHistoryQuery x,CancellationToken t) => queries.HistoryAsync(x.InstanceId,x.PageSize,x.After,t); }
internal sealed class GetClientContextQueryHandler(IManagedInstances instances,ICallContext calls) : IRequestHandler<GetClientContextQuery,ClientContext>
{ public Task<ClientContext> Handle(GetClientContextQuery x,CancellationToken t) => instances.ContextAsync(calls.Current!.Actor.InstanceId!.Value,t); }
