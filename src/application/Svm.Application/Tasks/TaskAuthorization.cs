using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Tasks;

namespace Svm.Application.Tasks;

internal sealed class TaskAuthorization(ITaskWorkflow tasks, ISoftwareCatalog software, IReleases releases,
    IUnitOfWork unit, TaskOptions options, IPersonnelWorkAuthorization authorization,
    ITaskServiceIdentity? service = null, IInstanceAccess? access = null, IAccessProofSource? proofs = null,
    IManagedInstances? instances = null)
{
    public async ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest x, PersonnelView? person, CancellationToken ct)
    {
        options.Validate(); var protect = unit.CurrentOperationId is not null;
        if (x.Context.Actor.Kind == ActorKind.Service)
        {
            var work = WorkId(x.Request);
            if (!TaskCapabilities.IsInternal(x.Request.GetType()) || service is null || service.SubjectId != x.Context.Actor.ActorId ||
                service.WorkId != work || x.Context.Actor.WorkId != work || x.Context.Actor.WorkOwner != ModuleOwner.Tasks) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
            var w = await tasks.AuthorityAsync(work, false, ct);
            // A revoked initiator must be observable by the trusted executor so it can persist a pause.
            await authorization.HasPermissionAsync(w.InitiatorId, w.SoftwareId, w.Kind is "Reschedule" or "Cancel" ? "deployment.control" : "deployment.create", protect, ct);
            if (!await software.ExistsAsync(w.SoftwareId, protect, ct)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            return AuthorizationDecision.Allow(AuthorizationTarget.Work(ModuleOwner.Tasks, work, w.SoftwareId));
        }
        var sid = await SoftwareId(x.Request, ct); if (sid is null) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if (x.Context.Actor.Kind == ActorKind.Instance)
        {
            var identity = await (access ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid)).AuthenticateAsync(proofs?.Proof ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid), protect, ct);
            if (identity?.Kind != ActorKind.Instance || identity.SoftwareId != sid || identity.InstanceId != x.Context.Actor.InstanceId) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            if ((await (instances ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid)).GetIdentityAsync(identity.InstanceId!.Value, protect, ct))?.Lifecycle != "Active") return AuthorizationDecision.Deny(RequestFailure.InstanceSuspended);
            var taskId = x.Request switch { ClaimInstanceTaskCommand t => t.TaskId, GetClientTaskQuery t => t.TaskId, _ => (Guid?)null };
            var attemptId = x.Request switch { StartInstanceTaskCommand t => t.AttemptId, SubmitTaskReceiptCommand t => t.AttemptId, _ => (Guid?)null };
            var task = taskId is { } tid ? await tasks.TaskAsync(tid, false, ct) : attemptId is { } aid ? await tasks.AttemptTaskAsync(aid, false, ct) : null;
            if (task is not null && task.InstanceId != identity.InstanceId) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            if (x.Request is StartInstanceTaskCommand && task is not null)
            { var d = await tasks.DeploymentAsync(task.DeploymentId, false, ct);
              if (!await authorization.HasPermissionAsync(d.AuthorizationSubjectId, sid.Value, "deployment.create", protect, ct)) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied); }
            if (!await software.ExistsAsync(sid.Value, protect, ct)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
            return AuthorizationDecision.Allow(AuthorizationTarget.Instance(sid.Value, identity.InstanceId!.Value));
        }
        if (person is null || !person.Permissions.Any(p => p.SoftwareId == sid && p.Operation == x.Policy.Permission)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if (x.Request is ControlInstanceTaskCommand { Action: "CloseUnknown" } && !person.Permissions.Any(p => p.SoftwareId == sid && p.Operation == "task.closeUnknown")) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        if (x.Request is ControlDeploymentCommand { Action: "Resume" } && !person.Permissions.Any(p => p.SoftwareId == sid && p.Operation == "deployment.create")) return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        var selectionId = x.Request switch { PutTargetChunkCommand t => t.SelectionId, SealTargetSelectionCommand t => t.SelectionId, GetTargetSelectionQuery t => t.SelectionId, _ => (Guid?)null };
        if (selectionId is { } sel && await tasks.OwnerAsync("selection", sel, ct) != person.SubjectId) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if (!await software.ExistsAsync(sid.Value, protect, ct)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if (x.Request is RecordIntegrationMaterialCommand material) await releases.GetAsync(material.ReleaseId, protect, ct);
        return AuthorizationDecision.Allow(AuthorizationTarget.Software(sid.Value));
    }
    internal static Guid WorkId(object request) => request switch { ClaimTaskWorkCommand c => c.WorkId, AdvanceTaskWorkCommand c => c.Lease.Work.WorkId,
        MaterializeTaskTargetsCommand c => c.Lease.Work.WorkId, FailTaskWorkCommand c => c.Lease.Work.WorkId, GetTaskAuthorityQuery q => q.WorkId, _ => Guid.Empty };
    private Task<Guid?> SoftwareId(object r, CancellationToken ct) => r switch
    {
        CreateTargetSelectionCommand x => System.Threading.Tasks.Task.FromResult<Guid?>(x.Input.SoftwareId),
        CreateDeploymentCommand x => System.Threading.Tasks.Task.FromResult<Guid?>(x.Input.SoftwareId),
        GetDeploymentCapabilitiesQuery x => System.Threading.Tasks.Task.FromResult<Guid?>(x.SoftwareId),
        ListDeploymentsQuery x => System.Threading.Tasks.Task.FromResult<Guid?>(x.Input.SoftwareId),
        ListInstanceTasksQuery x => System.Threading.Tasks.Task.FromResult<Guid?>(x.Input.SoftwareId),
        ListClientTasksQuery x => System.Threading.Tasks.Task.FromResult<Guid?>(x.Input.SoftwareId),
        PutTargetChunkCommand x => tasks.SoftwareForAsync("selection", x.SelectionId, ct), SealTargetSelectionCommand x => tasks.SoftwareForAsync("selection", x.SelectionId, ct), GetTargetSelectionQuery x => tasks.SoftwareForAsync("selection", x.SelectionId, ct),
        ControlDeploymentCommand x => tasks.SoftwareForAsync("deployment", x.DeploymentId, ct), CreateDeploymentControlWorkCommand x => tasks.SoftwareForAsync("deployment", x.DeploymentId, ct), GetDeploymentQuery x => tasks.SoftwareForAsync("deployment", x.DeploymentId, ct), ListDeploymentTargetsQuery x => tasks.SoftwareForAsync("deployment", x.DeploymentId, ct), GetDeploymentBatchesQuery x => tasks.SoftwareForAsync("deployment", x.DeploymentId, ct),
        ControlInstanceTaskCommand x => tasks.SoftwareForAsync("task", x.TaskId, ct), GetInstanceTaskQuery x => tasks.SoftwareForAsync("task", x.TaskId, ct), ClaimInstanceTaskCommand x => tasks.SoftwareForAsync("task", x.TaskId, ct), GetClientTaskQuery x => tasks.SoftwareForAsync("task", x.TaskId, ct), ListTaskReceiptsQuery x => tasks.SoftwareForAsync("task", x.TaskId, ct),
        StartInstanceTaskCommand x => tasks.SoftwareForAsync("attempt", x.AttemptId, ct), SubmitTaskReceiptCommand x => tasks.SoftwareForAsync("attempt", x.AttemptId, ct),
        GetTaskWorkQuery x => tasks.SoftwareForAsync("work", x.WorkId, ct), ListTaskControlItemsQuery x => tasks.SoftwareForAsync("work", x.WorkId, ct),
        RecordIntegrationMaterialCommand x => releases.SoftwareForAsync(x.ReleaseId, false, ct), GetIntegrationMaterialsQuery x => releases.SoftwareForAsync(x.ReleaseId, false, ct),
        _ => System.Threading.Tasks.Task.FromResult<Guid?>(null)
    };
}
