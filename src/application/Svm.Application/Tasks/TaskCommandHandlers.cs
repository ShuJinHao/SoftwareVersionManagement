using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Messaging.V1;
using Svm.Services.Contracts.Catalog;

namespace Svm.Application.Tasks;

internal sealed class CreateTargetSelectionCommandHandler(TaskOperations operations, ITaskWorkflow tasks, IIntegrationEventOutbox outbox, TaskDispatch dispatch) : IRequestHandler<CreateTargetSelectionCommand, OperationResult<SelectionView>>
{ public async Task<OperationResult<SelectionView>> Handle(CreateTargetSelectionCommand x, CancellationToken ct)
    { var result = await operations.CreateTargetSelection(x, ct); if (result.WorkId is { } id) await outbox.EnqueueAsync(dispatch.Preparation(await tasks.AuthorityAsync(id, false, ct), await dispatch.Now(ct)), ct); return result; } }
internal sealed class PutTargetChunkCommandHandler(TaskOperations operations) : IRequestHandler<PutTargetChunkCommand, OperationResult<SelectionView>>
{ public Task<OperationResult<SelectionView>> Handle(PutTargetChunkCommand x, CancellationToken ct) => operations.PutTargetChunk(x, ct); }
internal sealed class SealTargetSelectionCommandHandler(TaskOperations operations) : IRequestHandler<SealTargetSelectionCommand, OperationResult<SelectionView>>
{ public Task<OperationResult<SelectionView>> Handle(SealTargetSelectionCommand x, CancellationToken ct) => operations.SealTargetSelection(x, ct); }
internal sealed class CreateDeploymentCommandHandler(TaskOperations operations, ITaskWorkflow tasks, IIntegrationEventOutbox outbox, TaskDispatch dispatch) : IRequestHandler<CreateDeploymentCommand, OperationResult<DeploymentView>>
{ public async Task<OperationResult<DeploymentView>> Handle(CreateDeploymentCommand x, CancellationToken ct)
    { var result = await operations.CreateDeployment(x, ct); if (result.WorkId is { } id) await outbox.EnqueueAsync(dispatch.Preparation(await tasks.AuthorityAsync(id, false, ct), await dispatch.Now(ct)), ct); return result; } }
internal sealed class ControlDeploymentCommandHandler(TaskOperations operations, ITaskWorkflow tasks, IIntegrationEventOutbox outbox, TaskDispatch dispatch) : IRequestHandler<ControlDeploymentCommand, OperationResult<DeploymentView>>
{ public async Task<OperationResult<DeploymentView>> Handle(ControlDeploymentCommand x, CancellationToken ct)
    {
        var result = await operations.ControlDeployment(x, ct);
        if (x.Action == "Resume")
            foreach (var work in await tasks.ResumeDispatchesAsync(x.DeploymentId, ct))
            {
                var now = await dispatch.Now(ct);
                if (work.Kind is "Cancel" or "Reschedule") await outbox.EnqueueAsync(dispatch.Control(work, now), ct);
                else await outbox.EnqueueAsync(dispatch.Preparation(work, now), ct);
            }
        return result;
    } }
internal sealed class CreateDeploymentControlWorkCommandHandler(TaskOperations operations, ITaskWorkflow tasks, IIntegrationEventOutbox outbox, TaskDispatch dispatch) : IRequestHandler<CreateDeploymentControlWorkCommand, OperationResult<TaskWorkView>>
{ public async Task<OperationResult<TaskWorkView>> Handle(CreateDeploymentControlWorkCommand x, CancellationToken ct)
    { var result = await operations.CreateDeploymentControlWork(x, ct); await outbox.EnqueueAsync(dispatch.Control(await tasks.AuthorityAsync(result.WorkId!.Value, false, ct), await dispatch.Now(ct)), ct); return result; } }
internal sealed class ControlInstanceTaskCommandHandler(TaskOperations operations) : IRequestHandler<ControlInstanceTaskCommand, OperationResult<TaskView>>
{ public Task<OperationResult<TaskView>> Handle(ControlInstanceTaskCommand x, CancellationToken ct) => operations.ControlInstanceTask(x, ct); }
internal sealed class ClaimInstanceTaskCommandHandler(TaskOperations operations) : IRequestHandler<ClaimInstanceTaskCommand, OperationResult<Guid>>
{ public Task<OperationResult<Guid>> Handle(ClaimInstanceTaskCommand x, CancellationToken ct) => operations.ClaimInstanceTask(x, ct); }
internal sealed class StartInstanceTaskCommandHandler(TaskOperations operations) : IRequestHandler<StartInstanceTaskCommand, OperationResult<StartGrant>>
{ public Task<OperationResult<StartGrant>> Handle(StartInstanceTaskCommand x, CancellationToken ct) => operations.StartInstanceTask(x, ct); }
internal sealed class SubmitTaskReceiptCommandHandler(TaskOperations operations) : IRequestHandler<SubmitTaskReceiptCommand, OperationResult<ReceiptResult>>
{ public Task<OperationResult<ReceiptResult>> Handle(SubmitTaskReceiptCommand x, CancellationToken ct) => operations.SubmitTaskReceipt(x, ct); }
internal sealed class RecordIntegrationMaterialCommandHandler(TaskOperations operations) : IRequestHandler<RecordIntegrationMaterialCommand, OperationResult<IntegrationMaterialView>>
{ public Task<OperationResult<IntegrationMaterialView>> Handle(RecordIntegrationMaterialCommand x, CancellationToken ct) => operations.RecordIntegrationMaterial(x, ct); }
internal sealed class ClaimTaskWorkCommandHandler(TaskOperations operations) : IRequestHandler<ClaimTaskWorkCommand, OperationResult<TaskLease?>>
{ public Task<OperationResult<TaskLease?>> Handle(ClaimTaskWorkCommand x, CancellationToken ct) => operations.ClaimTaskWork(x, ct); }
internal sealed class AdvanceTaskWorkCommandHandler(TaskOperations operations) : IRequestHandler<AdvanceTaskWorkCommand, OperationResult<TaskWorkView>>
{ public Task<OperationResult<TaskWorkView>> Handle(AdvanceTaskWorkCommand x, CancellationToken ct) => operations.AdvanceTaskWork(x, ct); }
internal sealed class MaterializeTaskTargetsCommandHandler(TaskOperations operations) : IRequestHandler<MaterializeTaskTargetsCommand, OperationResult<SelectionView>>
{ public Task<OperationResult<SelectionView>> Handle(MaterializeTaskTargetsCommand x, CancellationToken ct) => operations.MaterializeTaskTargets(x, ct); }
internal sealed class FailTaskWorkCommandHandler(TaskOperations operations) : IRequestHandler<FailTaskWorkCommand, OperationResult<bool>>
{ public Task<OperationResult<bool>> Handle(FailTaskWorkCommand x, CancellationToken ct) => operations.FailTaskWork(x, ct); }

internal sealed class TaskDispatch(SiteCatalogOptions site, ITaskTime time)
{
    internal Task<DateTimeOffset> Now(CancellationToken ct) => time.NowAsync(ct);
    internal TaskPreparationAvailableV1 Preparation(TaskWorkAuthority w, DateTimeOffset now) => new(w.DispatchEventId, now, w.WorkId, null, site.Require().SiteId, w.SoftwareId, w.Kind == "TargetSelection" ? TaskPreparationWorkKind.TargetSelection : TaskPreparationWorkKind.Deployment, w.WorkId, w.DispatchSequence);
    internal TaskControlAvailableV1 Control(TaskWorkAuthority w, DateTimeOffset now) => new(w.DispatchEventId, now, w.WorkId, null, site.Require().SiteId, w.SoftwareId, w.Kind == "Cancel" ? TaskControlWorkKind.Cancel : TaskControlWorkKind.Reschedule, w.WorkId, w.DispatchSequence);
}
