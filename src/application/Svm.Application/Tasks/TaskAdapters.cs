using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using static Svm.Application.Tasks.TaskProjection;

namespace Svm.Application.Tasks;

internal abstract class TaskAdapter<T, V> : IIdempotencyRequestAdapter<T, OperationResult<V>> where T : notnull
{
    public abstract OperationRequestData Describe(T x);
    public virtual OperationResultReference GetReference(OperationResult<V> x) => new(x.OperationId, x.Status, x.ResourceId, x.WorkId);
    public abstract Task<OperationResult<V>> RestoreAsync(OperationResultReference r, CancellationToken ct);
    protected static Guid Id(OperationResultReference r) => r.ResourceId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    protected static OperationResult<V> Restored(OperationResultReference r, V v) => r.Status == OperationStatus.Accepted ? OperationResult<V>.Accepted(r.OperationId, r.WorkId!.Value, v, r.ResourceId) : OperationResult<V>.Completed(r.OperationId, v, r.ResourceId);
}
internal sealed class CreateTargetSelectionCommandAdapter(ITaskWorkflow tasks) : TaskAdapter<CreateTargetSelectionCommand, SelectionView>
{
    public override OperationRequestData Describe(CreateTargetSelectionCommand x) => new(x.Key, OperationValue.Object(), OperationValue.Object(G("softwareId", x.Input.SoftwareId), S("mode", x.Input.Mode), new("filter", Filter(x.Input.Filter))));
    public override async Task<OperationResult<SelectionView>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, await tasks.SelectionAsync(Id(r), false, ct));
}
internal sealed class SealTargetSelectionCommandAdapter(ITaskWorkflow tasks) : TaskAdapter<SealTargetSelectionCommand, SelectionView>
{
    public override OperationRequestData Describe(SealTargetSelectionCommand x) => new(x.Key, OperationValue.Object(G("selectionId", x.SelectionId)), OperationValue.Object(N("expectedRevision", x.ExpectedRevision), N("expectedChunkCount", x.ExpectedChunkCount), N("expectedMemberCount", x.ExpectedMemberCount)));
    public override async Task<OperationResult<SelectionView>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, await tasks.SelectionAsync(Id(r), false, ct));
}
internal sealed class CreateDeploymentCommandAdapter(ITaskWorkflow tasks) : TaskAdapter<CreateDeploymentCommand, DeploymentView>
{
    public override OperationRequestData Describe(CreateDeploymentCommand x) => new(x.Key, OperationValue.Object(), OperationValue.Object(G("softwareId", x.Input.SoftwareId), G("selectionId", x.Input.SelectionId), S("kind", x.Input.Kind), G("targetReleaseId", x.Input.TargetReleaseId), new("window", Window(x.Input.Window)), S("reason", x.Input.Reason), G("retryOfDeploymentId", x.Input.RetryOfDeploymentId)));
    public override async Task<OperationResult<DeploymentView>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, await tasks.DeploymentAsync(Id(r), false, ct));
}
internal sealed class ControlDeploymentCommandAdapter(ITaskWorkflow tasks) : TaskAdapter<ControlDeploymentCommand, DeploymentView>
{
    public override OperationRequestData Describe(ControlDeploymentCommand x) => new(x.Key, OperationValue.Object(G("deploymentId", x.DeploymentId)), OperationValue.Object(N("expectedRevision", x.ExpectedRevision), S("action", x.Action), S("reason", x.Reason), new("reviewedFailures", x.ReviewedFailures is null ? OperationValue.Null : OperationValue.Array(x.ReviewedFailures.Select(f => OperationValue.Object(G("batchId", f.BatchId), N("failureRevision", f.FailureRevision))).ToArray()))));
    public override async Task<OperationResult<DeploymentView>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, await tasks.DeploymentAsync(Id(r), false, ct));
}
internal sealed class CreateDeploymentControlWorkCommandAdapter(ITaskWorkflow tasks) : TaskAdapter<CreateDeploymentControlWorkCommand, TaskWorkView>
{
    public override OperationRequestData Describe(CreateDeploymentControlWorkCommand x) => new(x.Key, OperationValue.Object(G("deploymentId", x.DeploymentId)), OperationValue.Object(N("expectedRevision", x.ExpectedRevision), S("action", x.Action), S("reason", x.Reason), new("window", Window(x.Window))));
    public override async Task<OperationResult<TaskWorkView>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, await tasks.WorkAsync(r.WorkId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid), ct));
}
internal sealed class ControlInstanceTaskCommandAdapter(ITaskWorkflow tasks) : TaskAdapter<ControlInstanceTaskCommand, TaskView>
{
    public override OperationRequestData Describe(ControlInstanceTaskCommand x) => new(x.Key, OperationValue.Object(G("taskId", x.TaskId)), OperationValue.Object(N("expectedRevision", x.ExpectedRevision), S("action", x.Action), S("reason", x.Reason), S("onsiteEvidence", x.OnsiteEvidence), B("noActiveInstallationConfirmed", x.NoActiveInstallationConfirmed)));
    public override async Task<OperationResult<TaskView>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, await tasks.TaskAsync(Id(r), false, ct));
}
internal sealed class ClaimInstanceTaskCommandAdapter(ITaskWorkflow tasks) : TaskAdapter<ClaimInstanceTaskCommand, Guid>
{
    public override OperationRequestData Describe(ClaimInstanceTaskCommand x) => new(x.Key, OperationValue.Object(G("taskId", x.TaskId)), OperationValue.Object());
    public override async Task<OperationResult<Guid>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, (await tasks.TaskAsync(Id(r), false, ct)).AttemptId ?? throw new RequestRejectedException(RequestFailure.InvalidState));
}
internal sealed class StartInstanceTaskCommandAdapter(ITaskWorkflow tasks) : TaskAdapter<StartInstanceTaskCommand, StartGrant>
{
    public override OperationRequestData Describe(StartInstanceTaskCommand x) => new(x.Key, OperationValue.Object(G("attemptId", x.AttemptId)), OperationValue.Object(new("stateReport", Report(x.Input.StateReport)), S("downloadedSha256", x.Input.Preflight.DownloadedSha256), B("dataProtectionConfirmed", x.Input.Preflight.DataProtectionConfirmed), new("compatibilityDeclarationRevision", x.Input.Preflight.CompatibilityDeclarationRevision is { } rev ? OperationValue.Integer(rev) : OperationValue.Null), S("note", x.Input.Preflight.Note)));
    public override async Task<OperationResult<StartGrant>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, await tasks.ExistingGrantAsync(Id(r), ct) ?? throw new RequestRejectedException(RequestFailure.InvalidState));
}
internal sealed class RecordIntegrationMaterialCommandAdapter(IIntegrationMaterials materials) : TaskAdapter<RecordIntegrationMaterialCommand, IntegrationMaterialView>
{
    public override OperationRequestData Describe(RecordIntegrationMaterialCommand x) => new(x.Key, OperationValue.Object(G("releaseId", x.ReleaseId)), OperationValue.Object(new("dataLocations", Strings(x.Input.DataLocations)), S("updateBehavior", x.Input.UpdateBehavior), S("rollbackBehavior", x.Input.RollbackBehavior), S("recoveryPlan", x.Input.RecoveryPlan), S("verificationConclusion", x.Input.VerificationConclusion), new("evidenceReferences", Strings(x.Input.EvidenceReferences)), S("reason", x.Input.Reason)));
    public override OperationResultReference GetReference(OperationResult<IntegrationMaterialView> x) => new(x.OperationId, x.Status, x.Value.Id);
    public override async Task<OperationResult<IntegrationMaterialView>> RestoreAsync(OperationResultReference r, CancellationToken ct) => Restored(r, await materials.FindAsync(Id(r), ct));
}
internal sealed class PutTargetChunkCommandAdapter(ITaskWorkflow tasks) : IProtocolRequestAdapter<PutTargetChunkCommand, OperationResult<SelectionView>>
{
    public async Task<ProtocolResult<OperationResult<SelectionView>>?> FindCommittedAsync(PutTargetChunkCommand x, CancellationToken ct)
    { var s = await tasks.FindChunkAsync(x.SelectionId, x.ChunkNo, ChunkDigest(x.InstanceIds), ct); return s is null ? null : new(OperationResult<SelectionView>.Completed(s.Id, s, s.Id)); }
}
internal sealed class SubmitTaskReceiptCommandAdapter(ITaskWorkflow tasks) : IProtocolRequestAdapter<SubmitTaskReceiptCommand, OperationResult<ReceiptResult>>
{
    public async Task<ProtocolResult<OperationResult<ReceiptResult>>?> FindCommittedAsync(SubmitTaskReceiptCommand x, CancellationToken ct)
    { var r = await tasks.FindReceiptAsync(x.AttemptId, x.Input, ReceiptDigest(x.Input), ct); return r is null ? null : new(OperationResult<ReceiptResult>.Completed(r.ReceiptId, r, r.ReceiptId)); }
}
