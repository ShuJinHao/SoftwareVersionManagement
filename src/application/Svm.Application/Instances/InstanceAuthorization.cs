using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;

namespace Svm.Application.Instances;

internal sealed class InstanceAuthorization(IInstanceAccess access,IManagedInstances instances,IAccessProofSource proofs,ISiteAssets assets,
    ISoftwareCatalog software,IUnitOfWork unitOfWork,IHardwareAssets hardware, Svm.Application.Personnel.SiteAuthorization scope)
{
    private bool Protect => unitOfWork.CurrentOperationId is not null;
    internal async ValueTask<AuthorizationDecision> MachineAsync(AuthorizationRequest request,CancellationToken token)
    {
        var proof=proofs.Proof;
        if(proof is null) return AuthorizationDecision.Deny(RequestFailure.CredentialInvalid);
        var identity=await access.AuthenticateAsync(proof,Protect,token);
        var actor=request.Context.Actor;
        if(identity is null || identity.Kind!=actor.Kind || identity.SubjectId!=actor.ActorId || identity.SoftwareId!=actor.SoftwareId || identity.InstanceId!=actor.InstanceId)
            return AuthorizationDecision.Deny(RequestFailure.CredentialInvalid);
        await assets.EnsureDeploymentAsync(Protect,token);
        if(request.Request is RegisterInstanceCommand registration)
        {
            if(registration.SoftwareId!=identity.SoftwareId) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            // Read existence without reversing the REL -> INS write-lock order; the handler protects the mapping before creation.
            await assets.DeviceAsync(registration.DeviceId,false,token);
            await access.VerifyRegistrationAsync(registration,Protect,token);
            return AuthorizationDecision.Allow(AuthorizationTarget.Software(identity.SoftwareId));
        }
        if(request.Request is RecoverInstanceCommand recovery)
        {
            await access.VerifyRecoveryAsync(recovery,Protect,token);
            return AuthorizationDecision.Allow(AuthorizationTarget.Instance(identity.SoftwareId,identity.InstanceId!.Value));
        }
        if(identity.Kind!=ActorKind.Instance || identity.InstanceId is not { } id) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        var i=await instances.GetIdentityAsync(id,Protect,token);
        if(i is null || i.SoftwareId!=identity.SoftwareId) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if(i.Lifecycle!="Active") return AuthorizationDecision.Deny(RequestFailure.InstanceSuspended);
        return AuthorizationDecision.Allow(AuthorizationTarget.Instance(i.SoftwareId,id));
    }
    internal async ValueTask<AuthorizationDecision> HumanAsync(AuthorizationRequest request,PersonnelView person,CancellationToken token)
    {
        Guid? softwareId=null,instanceId=null;
        switch(request.Request)
        {
            case CreateEnrollmentGrantCommand x: softwareId=x.SoftwareId; break;
            case ListEnrollmentGrantsQuery x: softwareId=x.SoftwareId; break;
            case ListInstancesQuery x: softwareId=x.Input.Filter.SoftwareId; break;
            case RevokeEnrollmentGrantCommand x: softwareId=await access.SoftwareForAsync("enrollment",x.GrantId,Protect,token); break;
            case RevokeRecoveryGrantCommand x:
                softwareId=await access.SoftwareForAsync("recovery",x.GrantId,false,token);
                instanceId=(await access.GrantAsync(x.GrantId,true,token)).InstanceId; break;
            case RevokeInstanceCredentialCommand x:
                var c=await access.CredentialAsync(x.CredentialId,token); instanceId=c.SubjectId; softwareId=await access.SoftwareForAsync("instance",c.SubjectId,Protect,token); break;
            case CreateRecoveryGrantCommand x: instanceId=x.InstanceId; break;
            case BindInstanceHardwareCommand x:
                instanceId=x.InstanceId; break;
            case UpdateInstanceLifecycleCommand x: instanceId=x.InstanceId; break;
            case ListInstanceCredentialsQuery x: instanceId=x.InstanceId; break;
            case GetInstanceQuery x: instanceId=x.InstanceId; break;
            case GetInstanceHistoryQuery x: instanceId=x.InstanceId; break;
            default: return AuthorizationDecision.Deny(RequestFailure.ConfigurationInvalid);
        }
        if(instanceId is { } id)
        {
            // IAM guard precedes INS locks and races with recovery/revocation/stream operations.
            softwareId=await access.SoftwareForAsync("instance",id,Protect,token);
        }
        if(softwareId is not { } sid || !await scope.SoftwareAsync(person.Access,request.Policy.Permission!,sid,token))
            return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        await assets.EnsureDeploymentAsync(Protect,token);
        if(!await software.ExistsAsync(sid,Protect,token)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if(instanceId is { } instance)
        {
            // Protect the device/position before the instance, consistently with registration and reference protection.
            if (request.Request is BindInstanceHardwareCommand binding)
                await hardware.BindingAsync(binding.HardwareBindingId, Protect, token);
            var value=await instances.GetIdentityAsync(instance,Protect,token);
            if(value is null || value.SoftwareId!=sid) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            if (!await scope.DeviceAsync(person.Access, request.Policy.Permission!, value.DeviceId, sid, token) ||
                request.Request is BindInstanceHardwareCommand && !await scope.DeviceAsync(person.Access, "binding.manage", value.DeviceId, sid, token))
                return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            return AuthorizationDecision.Allow(AuthorizationTarget.Instance(sid,instance));
        }
        var devices = request.Request switch
        {
            CreateEnrollmentGrantCommand c => c.DeviceIds,
            RevokeEnrollmentGrantCommand c => (await access.GrantAsync(c.GrantId, false, token)).DeviceIds ?? [],
            _ => Array.Empty<Guid>()
        };
        foreach (var deviceId in devices)
            if (!await scope.DeviceAsync(person.Access, request.Policy.Permission!, deviceId, sid, token))
                return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        return AuthorizationDecision.Allow(AuthorizationTarget.Software(sid));
    }
}
