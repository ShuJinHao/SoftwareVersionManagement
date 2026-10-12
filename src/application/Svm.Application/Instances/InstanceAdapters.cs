using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;

namespace Svm.Application.Instances;

internal sealed class CreateEnrollmentGrantCommandAdapter(IInstanceAccess access,IPersonnelCrypto crypto) : IIdempotencyRequestAdapter<CreateEnrollmentGrantCommand,OperationResult<GrantView>>
{
    public OperationRequestData Describe(CreateEnrollmentGrantCommand x) => new(x.Key,OperationValue.Object(),OperationValue.Object(new OperationField("SoftwareId",OperationValue.Identifier(x.SoftwareId)),new OperationField("DeviceIds",OperationValue.Array(x.DeviceIds.Select(OperationValue.Identifier).ToArray())),new OperationField("ExpiresAt",OperationValue.Timestamp(x.ExpiresAt)),new OperationField("MaxInstances",OperationValue.Integer(x.MaxInstances)),new OperationField("SecretMaterial",OperationValue.Text(crypto.HashSecret(x.SecretMaterial))),new OperationField("Reason",OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<GrantView> x) => new(x.OperationId,x.Status,x.ResourceId);
    public async Task<OperationResult<GrantView>> RestoreAsync(OperationResultReference r,CancellationToken t)
    {
        var id=r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var v=await access.GrantAsync(id,false,t);
        return OperationResult<GrantView>.Completed(r.OperationId,v,id);
    }
}
internal sealed class RevokeEnrollmentGrantCommandAdapter(IInstanceAccess access) : IIdempotencyRequestAdapter<RevokeEnrollmentGrantCommand,OperationResult<GrantView>>
{
    public OperationRequestData Describe(RevokeEnrollmentGrantCommand x) => new(x.Key,OperationValue.Object(new OperationField("GrantId",OperationValue.Identifier(x.GrantId))),OperationValue.Object(new OperationField("ExpectedRevision",OperationValue.Integer(x.ExpectedRevision)),new OperationField("Reason",OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<GrantView> x) => new(x.OperationId,x.Status,x.ResourceId);
    public async Task<OperationResult<GrantView>> RestoreAsync(OperationResultReference r,CancellationToken t)
    {
        var id=r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var v=await access.GrantAsync(id,false,t);
        return OperationResult<GrantView>.Completed(r.OperationId,v,id);
    }
}
internal sealed class CreateRecoveryGrantCommandAdapter(IInstanceAccess access,IPersonnelCrypto crypto) : IIdempotencyRequestAdapter<CreateRecoveryGrantCommand,OperationResult<GrantView>>
{
    public OperationRequestData Describe(CreateRecoveryGrantCommand x) => new(x.Key,OperationValue.Object(new OperationField("InstanceId",OperationValue.Identifier(x.InstanceId))),OperationValue.Object(new OperationField("ExpiresAt",OperationValue.Timestamp(x.ExpiresAt)),new OperationField("SecretMaterial",OperationValue.Text(crypto.HashSecret(x.SecretMaterial))),new OperationField("Reason",OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<GrantView> x) => new(x.OperationId,x.Status,x.ResourceId);
    public async Task<OperationResult<GrantView>> RestoreAsync(OperationResultReference r,CancellationToken t)
    {
        var id=r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var v=await access.GrantAsync(id,true,t);
        return OperationResult<GrantView>.Completed(r.OperationId,v,id);
    }
}
internal sealed class RevokeRecoveryGrantCommandAdapter(IInstanceAccess access) : IIdempotencyRequestAdapter<RevokeRecoveryGrantCommand,OperationResult<GrantView>>
{
    public OperationRequestData Describe(RevokeRecoveryGrantCommand x) => new(x.Key,OperationValue.Object(new OperationField("GrantId",OperationValue.Identifier(x.GrantId))),OperationValue.Object(new OperationField("ExpectedRevision",OperationValue.Integer(x.ExpectedRevision)),new OperationField("Reason",OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<GrantView> x) => new(x.OperationId,x.Status,x.ResourceId);
    public async Task<OperationResult<GrantView>> RestoreAsync(OperationResultReference r,CancellationToken t)
    {
        var id=r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var v=await access.GrantAsync(id,true,t);
        return OperationResult<GrantView>.Completed(r.OperationId,v,id);
    }
}
internal sealed class RevokeInstanceCredentialCommandAdapter(IInstanceAccess access) : IIdempotencyRequestAdapter<RevokeInstanceCredentialCommand,OperationResult<CredentialView>>
{
    public OperationRequestData Describe(RevokeInstanceCredentialCommand x) => new(x.Key,OperationValue.Object(new OperationField("CredentialId",OperationValue.Identifier(x.CredentialId))),OperationValue.Object(new OperationField("ExpectedRevision",OperationValue.Integer(x.ExpectedRevision)),new OperationField("Reason",OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<CredentialView> x) => new(x.OperationId,x.Status,x.ResourceId);
    public async Task<OperationResult<CredentialView>> RestoreAsync(OperationResultReference r,CancellationToken t)
    {
        var id=r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var v=await access.CredentialAsync(id,t);
        return OperationResult<CredentialView>.Completed(r.OperationId,v,id);
    }
}
internal sealed class UpdateInstanceLifecycleCommandAdapter(IManagedInstances instances) : IIdempotencyRequestAdapter<UpdateInstanceLifecycleCommand,OperationResult<InstanceIdentity>>
{
    public OperationRequestData Describe(UpdateInstanceLifecycleCommand x) => new(x.Key,OperationValue.Object(new OperationField("InstanceId",OperationValue.Identifier(x.InstanceId))),OperationValue.Object(new OperationField("ExpectedRevision",OperationValue.Integer(x.ExpectedRevision)),new OperationField("Lifecycle",OperationValue.Text(x.Lifecycle)),new OperationField("Reason",OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<InstanceIdentity> x) => new(x.OperationId,x.Status,x.ResourceId);
    public async Task<OperationResult<InstanceIdentity>> RestoreAsync(OperationResultReference r,CancellationToken t)
    {
        var id=r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var v=await instances.GetIdentityAsync(id,false,t) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        return OperationResult<InstanceIdentity>.Completed(r.OperationId,v,id);
    }
}
internal sealed class OpenReportStreamCommandAdapter(IManagedInstances instances) : IIdempotencyRequestAdapter<OpenReportStreamCommand,OperationResult<StreamResult>>
{
    public OperationRequestData Describe(OpenReportStreamCommand x) => new(x.Key,OperationValue.Object(),OperationValue.Object(new OperationField("ExpectedEpoch",OperationValue.Integer(x.ExpectedEpoch))));
    public OperationResultReference GetReference(OperationResult<StreamResult> x) => new(x.OperationId,x.Status,x.ResourceId);
    public async Task<OperationResult<StreamResult>> RestoreAsync(OperationResultReference r,CancellationToken t)
    {
        var id=r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var v=await instances.StreamResultAsync(r.OperationId,t);
        return OperationResult<StreamResult>.Completed(r.OperationId,v,id);
    }
}
internal sealed class RegisterInstanceCommandAdapter(IInstanceAccess access) : IProtocolRequestAdapter<RegisterInstanceCommand,OperationResult<RegistrationResult>>
{
    public async Task<ProtocolResult<OperationResult<RegistrationResult>>?> FindCommittedAsync(RegisterInstanceCommand x,CancellationToken t)
    { var v=await access.FindRegistrationAsync(x,t); return v is null ? null : new(OperationResult<RegistrationResult>.Completed(v.InstanceId,v,v.InstanceId)); }
}
internal sealed class RecoverInstanceCommandAdapter(IInstanceAccess access) : IProtocolRequestAdapter<RecoverInstanceCommand,OperationResult<RegistrationResult>>
{
    public async Task<ProtocolResult<OperationResult<RegistrationResult>>?> FindCommittedAsync(RecoverInstanceCommand x,CancellationToken t)
    { var v=await access.FindRecoveryAsync(x,t); return v is null ? null : new(OperationResult<RegistrationResult>.Completed(v.InstanceId,v,v.InstanceId)); }
}
internal sealed class SubmitStatusReportCommandAdapter(IManagedInstances instances,ICallContext calls) : IProtocolRequestAdapter<SubmitStatusReportCommand,OperationResult<ReportResult>>
{
    public async Task<ProtocolResult<OperationResult<ReportResult>>?> FindCommittedAsync(SubmitStatusReportCommand x,CancellationToken t)
    { var id=calls.Current!.Actor.InstanceId!.Value; var v=await instances.FindReportAsync(id,x.Report,t); return v is null ? null : new(OperationResult<ReportResult>.Completed(id,v,id)); }
}
