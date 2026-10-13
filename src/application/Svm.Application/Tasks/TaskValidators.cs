using FluentValidation;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Instances;

namespace Svm.Application.Tasks;

internal static class TaskValidation
{
    internal static bool Reason(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 256;
    internal static bool Window(TaskWindow? w) => w is null || w.NotBefore.Offset == TimeSpan.Zero && w.LatestStart.Offset == TimeSpan.Zero && w.NotBefore < w.LatestStart;
    internal static bool List(TaskListInput x) => x.SoftwareId != Guid.Empty && x.BatchId != Guid.Empty && (x.WaitReason is null or "WindowMissed" or "Paused" or "NoHealthyPackage" or "CompatibilityUnknown" or "PrecheckRejected" or "ReceiptTimeout") && x.PageSize is >= 1 and <= 200 && (x.State is null || new[] {"Preparing","Running","Paused","Rejected","Completed","Canceled","Queued","Available","AwaitingResult","Succeeded","Failed","ClosedUnknown"}.Contains(x.State));
}
internal sealed class CreateTargetSelectionCommandValidator : AbstractValidator<CreateTargetSelectionCommand>
{
    public CreateTargetSelectionCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.Input.SoftwareId).NotEmpty(); RuleFor(x => x.Input.Mode).Must(x => x is "Explicit" or "Filter"); RuleFor(x => x.Input.Filter).Must(x => x is null || (x.SoftwareId != Guid.Empty && x.ProcessId != Guid.Empty && x.DeviceId != Guid.Empty && x.InstalledReleaseId != Guid.Empty && (x.DeviceNo is null || x.DeviceNo.Length <= 128) && (x.ReportedIp is null || System.Net.IPAddress.TryParse(x.ReportedIp,out _)) && (x.Freshness is null or "Fresh" or "Unknown" or "NeverReported") && (x.RunningState is null or "Running" or "Stopped" or "Unknown") && (x.Lifecycle is null or "Active" or "Suspended"))); RuleFor(x => x.Input).Must(x => x.Mode == "Explicit" ? x.Filter is null : x.Filter is not null && x.Filter.SoftwareId == x.SoftwareId); }
}
internal sealed class PutTargetChunkCommandValidator : AbstractValidator<PutTargetChunkCommand>
{
    public PutTargetChunkCommandValidator() { RuleFor(x => x.SelectionId).NotEmpty(); RuleFor(x => x.ChunkNo).GreaterThanOrEqualTo(0); RuleFor(x => x.InstanceIds).NotNull().Must(x => x is not null && x.Count is >= 1 and <= 1000 && x.All(i => i != Guid.Empty)); }
}
internal sealed class SealTargetSelectionCommandValidator : AbstractValidator<SealTargetSelectionCommand>
{
    public SealTargetSelectionCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.SelectionId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0); RuleFor(x => x.ExpectedChunkCount).GreaterThan(0); RuleFor(x => x.ExpectedMemberCount).GreaterThan(0); }
}
internal sealed class CreateDeploymentCommandValidator : AbstractValidator<CreateDeploymentCommand>
{
    public CreateDeploymentCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.Input.SoftwareId).NotEmpty(); RuleFor(x => x.Input.SelectionId).NotEmpty(); RuleFor(x => x.Input.Kind).Equal("Update"); RuleFor(x => x.Input.Reason).Must(TaskValidation.Reason); RuleFor(x => x.Input.Window).Must(TaskValidation.Window); }
}
internal sealed class ControlDeploymentCommandValidator : AbstractValidator<ControlDeploymentCommand>
{
    public ControlDeploymentCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.DeploymentId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0); RuleFor(x => x.Action).Must(x => x is "Pause" or "Resume"); RuleFor(x => x.Reason).Must(TaskValidation.Reason); RuleFor(x => x.ReviewedFailures).Must(x => x is null || x.Count <= 200 && x.All(i=>i is not null && i.BatchId != Guid.Empty && i.FailureRevision >= 0)); }
}
internal sealed class CreateDeploymentControlWorkCommandValidator : AbstractValidator<CreateDeploymentControlWorkCommand>
{
    public CreateDeploymentControlWorkCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.DeploymentId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0); RuleFor(x => x.Action).Must(x => x is "Reschedule" or "Cancel"); RuleFor(x => x.Reason).Must(TaskValidation.Reason); RuleFor(x => x.Window).Must(TaskValidation.Window); RuleFor(x => x).Must(x => x.Action == "Reschedule" ? x.Window is not null : x.Window is null); }
}
internal sealed class ControlInstanceTaskCommandValidator : AbstractValidator<ControlInstanceTaskCommand>
{
    public ControlInstanceTaskCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.TaskId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0); RuleFor(x => x.Action).Must(x => x is "Defer" or "Restore" or "Cancel" or "CloseUnknown"); RuleFor(x => x.Reason).Must(TaskValidation.Reason); RuleFor(x => x).Must(x => x.Action != "CloseUnknown" || x.NoActiveInstallationConfirmed && TaskValidation.Reason(x.OnsiteEvidence)); }
}
internal sealed class ClaimInstanceTaskCommandValidator : AbstractValidator<ClaimInstanceTaskCommand>
{
    public ClaimInstanceTaskCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.TaskId).NotEmpty(); }
}
internal sealed class StartInstanceTaskCommandValidator : AbstractValidator<StartInstanceTaskCommand>
{
    public StartInstanceTaskCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.AttemptId).NotEmpty(); RuleFor(x => x.Input.Preflight).NotNull(); RuleFor(x => x.Input.StateReport).NotNull(); When(x => x.Input.Preflight is not null, () => { RuleFor(x => x.Input.Preflight.DownloadedSha256).NotEmpty().Matches("^[0-9a-f]{64}$"); RuleFor(x => x.Input.Preflight.DataProtectionConfirmed).Equal(true); RuleFor(x => x.Input.Preflight.CompatibilityDeclarationRevision).Null(); RuleFor(x => x.Input.Preflight.Note).MaximumLength(2000); }); RuleFor(x => x.Input.StateReport).SetValidator(new TaskStateReportValidator()); }
}
internal sealed class SubmitTaskReceiptCommandValidator : AbstractValidator<SubmitTaskReceiptCommand>
{
    public SubmitTaskReceiptCommandValidator() { RuleFor(x => x.AttemptId).NotEmpty(); RuleFor(x => x.Input.EventId).NotEmpty(); RuleFor(x => x.Input.Sequence).GreaterThan(0); RuleFor(x => x.Input.Kind).Must(x => x is "Progress" or "Terminal"); RuleFor(x => x.Input).Must(x => x.Kind == "Progress" ? x.Progress is "Downloading" or "ReadyToInstall" or "Installing" && x.Result is null && x.StateReport is null : x.Result is "Succeeded" or "Failed" or "NotStarted" && x.Progress is null && (x.Result != "Failed" || TaskValidation.Reason(x.FailureCode) && TaskValidation.Reason(x.Detail))); RuleFor(x => x.Input.FailureCode).MaximumLength(128); RuleFor(x => x.Input.Detail).MaximumLength(2000); When(x => x.Input.StateReport is not null, () => RuleFor(x => x.Input.StateReport!).SetValidator(new TaskStateReportValidator())); }
}
internal sealed class RecordIntegrationMaterialCommandValidator : AbstractValidator<RecordIntegrationMaterialCommand>
{
    public RecordIntegrationMaterialCommandValidator() { RuleFor(x => x.Key).NotEmpty(); RuleFor(x => x.ReleaseId).NotEmpty(); RuleFor(x => x.Input.DataLocations).Must(x => x is not null && x.Count is >= 1 and <= 50 && x.All(TaskValidation.Reason)); RuleFor(x => x.Input.UpdateBehavior).Must(TaskValidation.Reason); RuleFor(x => x.Input.RollbackBehavior).Must(TaskValidation.Reason); RuleFor(x => x.Input.RecoveryPlan).Must(TaskValidation.Reason); RuleFor(x => x.Input.Reason).Must(TaskValidation.Reason); RuleFor(x => x.Input.EvidenceReferences).Must(x => x is not null && x.Count <= 50 && x.All(TaskValidation.Reason)); RuleFor(x => x.Input.VerificationConclusion).MaximumLength(2000); }
}
internal sealed class ClaimTaskWorkCommandValidator : AbstractValidator<ClaimTaskWorkCommand>
{
    public ClaimTaskWorkCommandValidator() { RuleFor(x => x.WorkId).NotEmpty(); RuleFor(x => x.LeaseToken).NotEmpty(); }
}
internal sealed class AdvanceTaskWorkCommandValidator : AbstractValidator<AdvanceTaskWorkCommand>
{
    public AdvanceTaskWorkCommandValidator() { RuleFor(x => x.Lease.Token).NotEmpty(); RuleFor(x => x.Lease.Work.WorkId).NotEmpty(); RuleFor(x => x.Lease.Generation).GreaterThan(0); }
}
internal sealed class MaterializeTaskTargetsCommandValidator : AbstractValidator<MaterializeTaskTargetsCommand>
{
    public MaterializeTaskTargetsCommandValidator() { RuleFor(x => x.Lease.Token).NotEmpty(); RuleFor(x => x.Lease.Work.WorkId).NotEmpty(); RuleFor(x => x.Lease.Generation).GreaterThan(0); }
}
internal sealed class FailTaskWorkCommandValidator : AbstractValidator<FailTaskWorkCommand>
{
    public FailTaskWorkCommandValidator() { RuleFor(x => x.Lease.Token).NotEmpty(); RuleFor(x => x.Lease.Work.WorkId).NotEmpty(); RuleFor(x => x.Lease.Generation).GreaterThan(0); RuleFor(x => x.Code).NotEmpty().MaximumLength(128); }
}
internal sealed class GetDeploymentCapabilitiesQueryValidator : AbstractValidator<GetDeploymentCapabilitiesQuery>
{
    public GetDeploymentCapabilitiesQueryValidator() { RuleFor(x => x.SoftwareId).NotEmpty(); }
}
internal sealed class GetTargetSelectionQueryValidator : AbstractValidator<GetTargetSelectionQuery>
{
    public GetTargetSelectionQueryValidator() { RuleFor(x => x.SelectionId).NotEmpty(); }
}
internal sealed class GetDeploymentQueryValidator : AbstractValidator<GetDeploymentQuery>
{
    public GetDeploymentQueryValidator() { RuleFor(x => x.DeploymentId).NotEmpty(); }
}
internal sealed class ListDeploymentsQueryValidator : AbstractValidator<ListDeploymentsQuery>
{
    public ListDeploymentsQueryValidator() { RuleFor(x => x.Input).Must(TaskValidation.List); }
}
internal sealed class ListDeploymentTargetsQueryValidator : AbstractValidator<ListDeploymentTargetsQuery>
{
    public ListDeploymentTargetsQueryValidator() { RuleFor(x => x.Decision).Must(x=>x is null or "Accepted" or "Rejected"); RuleFor(x => x.ReasonCode).MaximumLength(128); RuleFor(x => x.DeploymentId).NotEmpty(); RuleFor(x => x.PageSize).InclusiveBetween(1,200); }
}
internal sealed class GetDeploymentBatchesQueryValidator : AbstractValidator<GetDeploymentBatchesQuery>
{
    public GetDeploymentBatchesQueryValidator() { RuleFor(x => x.DeploymentId).NotEmpty(); }
}
internal sealed class ListInstanceTasksQueryValidator : AbstractValidator<ListInstanceTasksQuery>
{
    public ListInstanceTasksQueryValidator() { RuleFor(x => x.Input).Must(TaskValidation.List); }
}
internal sealed class GetInstanceTaskQueryValidator : AbstractValidator<GetInstanceTaskQuery>
{
    public GetInstanceTaskQueryValidator() { RuleFor(x => x.TaskId).NotEmpty(); }
}
internal sealed class ListTaskReceiptsQueryValidator : AbstractValidator<ListTaskReceiptsQuery>
{
    public ListTaskReceiptsQueryValidator() { RuleFor(x => x.TaskId).NotEmpty(); RuleFor(x => x.PageSize).InclusiveBetween(1,200); }
}
internal sealed class GetTaskWorkQueryValidator : AbstractValidator<GetTaskWorkQuery>
{
    public GetTaskWorkQueryValidator() { RuleFor(x => x.WorkId).NotEmpty(); }
}
internal sealed class ListTaskControlItemsQueryValidator : AbstractValidator<ListTaskControlItemsQuery>
{
    public ListTaskControlItemsQueryValidator() { RuleFor(x => x.WorkId).NotEmpty(); RuleFor(x => x.PageSize).InclusiveBetween(1,200); }
}
internal sealed class ListClientTasksQueryValidator : AbstractValidator<ListClientTasksQuery>
{
    public ListClientTasksQueryValidator() { RuleFor(x => x.Input).Must(TaskValidation.List); }
}
internal sealed class GetClientTaskQueryValidator : AbstractValidator<GetClientTaskQuery>
{
    public GetClientTaskQueryValidator() { RuleFor(x => x.TaskId).NotEmpty(); }
}
internal sealed class GetTaskAuthorityQueryValidator : AbstractValidator<GetTaskAuthorityQuery>
{
    public GetTaskAuthorityQueryValidator() { RuleFor(x => x.WorkId).NotEmpty(); }
}
internal sealed class GetIntegrationMaterialsQueryValidator : AbstractValidator<GetIntegrationMaterialsQuery>
{
    public GetIntegrationMaterialsQueryValidator() { RuleFor(x => x.ReleaseId).NotEmpty(); RuleFor(x => x.PageSize).InclusiveBetween(1,200); }
}
internal sealed class TaskStateReportValidator : AbstractValidator<StateReport>
{
    public TaskStateReportValidator() { RuleFor(x => x).Must(Svm.Services.Contracts.Instances.InstanceValidation.Report); }
}
