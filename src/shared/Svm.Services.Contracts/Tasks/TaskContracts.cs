using System.Text.Json.Serialization;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;

namespace Svm.Services.Contracts.Tasks;

/// <summary>No deployment defaults are inferred. All operating limits belong to this deployment's private configuration.</summary>
public sealed record TaskOptions(string? DefaultStartLocalTime = null, string? DefaultLatestStartLocalTime = null,
    int? BatchSize = null, int? FailureLimit = null, int? ResultWaitSeconds = null, int? SelectionChunkSize = null,
    int? LeaseSeconds = null, int? PollRetrySeconds = null, int? MaxSelectionMembers = null, int? SnapshotTimeoutSeconds = null)
{
    public void Validate()
    {
        if (!TimeOnly.TryParseExact(DefaultStartLocalTime, "HH:mm", out var start) ||
            !TimeOnly.TryParseExact(DefaultLatestStartLocalTime, "HH:mm", out var end) || start == end ||
            BatchSize is null or < 1 or > 1000 || FailureLimit is null or < 1 || FailureLimit > BatchSize ||
            ResultWaitSeconds is null or < 1 or > 604800 || SelectionChunkSize is null or < 1 or > 100 ||
            LeaseSeconds is null or < 10 or > 600 || PollRetrySeconds is null or < 1 or > 60 ||
            MaxSelectionMembers is null or < 1 or > 100000 || SnapshotTimeoutSeconds is null or < 1 or > 300 || SnapshotTimeoutSeconds + 5 >= LeaseSeconds)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
}
public sealed record DeploymentCapabilities(string DefaultStartLocalTime, string DefaultLatestStartLocalTime, string TimeZone,
    int BatchSize, int FailureLimit, int ResultWaitSeconds, int SelectionChunkSize, int MaxSelectionMembers, int PollRetrySeconds);
public sealed record TaskWindow([property: JsonRequired] DateTimeOffset NotBefore, [property: JsonRequired] DateTimeOffset LatestStart);
public sealed record TaskParameters(int BatchSize, int FailureLimit, int ResultWaitSeconds);
public sealed record SelectionInput([property: JsonRequired] Guid SoftwareId, [property: JsonRequired] string Mode, InstanceFilter? Filter = null);
public sealed record SelectionView(Guid Id, Guid SoftwareId, string Mode, string State, int MemberCount,
    int ReceivedChunkCount, DateTimeOffset? SnapshotAt, long Revision);
public sealed record DeploymentInput([property: JsonRequired] Guid SoftwareId, [property: JsonRequired] Guid SelectionId, [property: JsonRequired] string Kind, Guid? TargetReleaseId,
    TaskWindow? Window, [property: JsonRequired] string Reason, Guid? RetryOfDeploymentId = null);
public sealed record ResultCounts(int Succeeded, int Failed, int Canceled, int ClosedUnknown, int Unfinished);
public sealed record PauseReason(string Code, string Scope);
public sealed record DeploymentView(Guid Id, Guid SoftwareId, Guid TargetReleaseId, string Kind, string State,
    Guid AuthorizationSubjectId, TaskWindow Window, TaskParameters Parameters, Guid SelectionId, int SelectedCount,
    int ProcessedCount, int AcceptedCount, int RejectedCount, ResultCounts ResultCounts,
    IReadOnlyList<PauseReason> PauseReasons, bool ControlPending, long Revision);
public sealed record AdmissionView(Guid InstanceId, string Decision, string? ReasonCode, Guid? TaskId);
public sealed record BatchView(Guid Id, int Ordinal, string State, DateTimeOffset? OpenedAt, int TaskCount,
    int DeferredCount, int FailedCount, long FailureRevision, long ReviewedFailureRevision, int TimedOutCount,
    DateTimeOffset? EarliestResponseDeadlineAt, long Revision);
public sealed record ManualClosureView(Guid SubjectId, string Reason, string OnsiteEvidence, DateTimeOffset ClosedAt);
public sealed record TaskView(Guid Id, Guid DeploymentId, Guid? BatchId, Guid InstanceId, Guid TargetReleaseId,
    string State, TaskWindow Window, string? WaitReason, bool IsDeferred, DateTimeOffset? ResponseDeadlineAt,
    Guid? AttemptId, DateTimeOffset? StartAuthorizedAt, DateTimeOffset? MustBeginBefore, string? LastReportedProgress,
    string? TerminalResult, ManualClosureView? ManualClosure, Guid? RetryOfTaskId, long Revision);
public sealed record FailureReview([property: JsonRequired] Guid BatchId, [property: JsonRequired] long FailureRevision);
public sealed record StartPreflight([property: JsonRequired] string DownloadedSha256, [property: JsonRequired] bool DataProtectionConfirmed, long? CompatibilityDeclarationRevision = null, string? Note = null);
public sealed record StartInput([property: JsonRequired] StateReport StateReport, [property: JsonRequired] StartPreflight Preflight);
public sealed record StartGrant(Guid TaskId, Guid AttemptId, Guid TargetReleaseId, Guid PackageId, string Sha256,
    DateTimeOffset StartAuthorizedAt, DateTimeOffset MustBeginBefore);
public sealed record ReceiptInput([property: JsonRequired] Guid EventId, [property: JsonRequired] long Sequence, [property: JsonRequired] string Kind, string? Progress = null, string? Result = null,
    string? FailureCode = null, string? Detail = null, StateReport? StateReport = null);
public sealed record ReceiptView(Guid Id, Guid AttemptId, Guid EventId, long Sequence, string Kind, string? Progress,
    string? Result, string? FailureCode, string? Detail, DateTimeOffset ReceivedAt, bool Applied, bool LateAfterClosure);
public sealed record ReceiptResult(Guid ReceiptId, Guid TaskId, Guid AttemptId, bool Applied, bool LateAfterClosure,
    string TaskState, bool? StateReportApplied);
public sealed record TaskWorkView(Guid Id, Guid SoftwareId, string Kind, string State, string Stage, Guid? Cursor,
    int ProcessedItems, string? LastErrorCode, long Revision);
public sealed record ControlItemView(Guid TaskId, string Outcome, string? ReasonCode);
public sealed record TaskPage<T>(IReadOnlyList<T> Items, Guid? Next);
public sealed record TaskListInput(Guid SoftwareId, Guid? DeploymentId = null, string? State = null, int PageSize = 50, Guid? After = null, Guid? BatchId = null, string? WaitReason = null, bool? IsDeferred = null);
public sealed record TaskSummary(Guid TaskId, string? Result);
public interface ITaskQueries
{
    Task<SelectionView> SelectionAsync(Guid id, CancellationToken token);
    Task<DeploymentView> DeploymentAsync(Guid id, CancellationToken token);
    Task<TaskView> TaskAsync(Guid id, CancellationToken token);
    Task<TaskWorkView> WorkAsync(Guid id, CancellationToken token);
    Task<TaskPage<BatchView>> BatchesAsync(Guid deployment, int take, Guid? after, CancellationToken token);
    Task<IReadOnlyList<IntegrationMaterialView>> MaterialsAsync(Guid release, int take, Guid? after, CancellationToken token);
    Task<TaskPage<DeploymentView>> DeploymentsAsync(TaskListInput input, CancellationToken token);
    Task<TaskPage<AdmissionView>> AdmissionsAsync(Guid deploymentId, int take, Guid? after, string? decision, string? reasonCode, CancellationToken token);
    Task<TaskPage<TaskView>> TasksAsync(TaskListInput input, Guid? instanceId, CancellationToken token);
    Task<TaskPage<ReceiptView>> ReceiptsAsync(Guid taskId, int take, Guid? after, CancellationToken token);
    Task<TaskPage<ControlItemView>> ControlItemsAsync(Guid workId, int take, Guid? after, CancellationToken token);
    Task<IReadOnlyDictionary<Guid, TaskSummary>> LatestAsync(IReadOnlyList<Guid> instanceIds, CancellationToken token);
}
/// <summary>INS supplies bounded facts; snapshot pages are consumed only inside the framework-owned repeatable-read root.</summary>
public sealed record InstanceTaskFact(Guid Id, Guid SoftwareId, string Lifecycle, StateReport? Snapshot);
public interface IInstanceTaskFacts
{
    Task<InstanceTaskFact?> GetAsync(Guid instanceId, bool protect, CancellationToken token);
    Task<IReadOnlyList<Guid>> SnapshotPageAsync(InstanceFilter filter, Guid? after, int take, CancellationToken token);
}
public interface ITargetSnapshotContext { bool IsMaterializing { get; } int TimeoutSeconds { get; } }
/// <summary>Production time is read from the current database transaction; tests can provide a controlled clock.</summary>
public interface ITaskTime { Task<DateTimeOffset> NowAsync(CancellationToken token); }
public sealed record TaskWorkAuthority(Guid WorkId, Guid SoftwareId, Guid ResourceId, Guid InitiatorId, string Kind,
    long DispatchSequence, Guid DispatchEventId, bool Accepted, string State, string Stage,
    Guid? LeaseToken, long LeaseGeneration, DateTimeOffset? LeaseUntil);
public sealed record TaskLease(TaskWorkAuthority Work, Guid Token, long Generation);
public interface ITaskServiceIdentity { Guid SubjectId { get; } Guid? WorkId { get; } }
public interface ITaskWorkflow
{
    Task<Guid?> SoftwareForAsync(string resource, Guid id, CancellationToken token);
    Task<InstanceFilter> FilterAsync(Guid selectionId, CancellationToken token);
    Task<Guid> OwnerAsync(string resource, Guid id, CancellationToken token);
    Task<SelectionView> SelectionAsync(Guid id, bool protect, CancellationToken token);
    Task<SelectionView> CreateSelectionAsync(Guid id, SelectionInput input, Guid actor, CancellationToken token);
    Task<SelectionView> ChunkAsync(Guid id, int number, IReadOnlyList<Guid> instances, string digest, CancellationToken token);
    Task<SelectionView?> FindChunkAsync(Guid id, int number, string digest, CancellationToken token);
    Task<SelectionView> SealAsync(Guid id, long revision, int chunks, int count, CancellationToken token);
    Task AddSnapshotMembersAsync(Guid id, IReadOnlyList<Guid> ids, CancellationToken token);
    Task CompleteSnapshotAsync(Guid id, DateTimeOffset at, CancellationToken token);
    Task<DeploymentView> CreateAsync(Guid id, DeploymentInput input, Guid releaseId, Guid actor, TaskWindow window, CancellationToken token);
    Task<DeploymentView> DeploymentAsync(Guid id, bool protect, CancellationToken token);
    Task<IReadOnlyList<BatchView>> BatchesAsync(Guid id, CancellationToken token);
    Task<TaskView> TaskAsync(Guid id, bool protect, CancellationToken token);
    Task<TaskView> AttemptTaskAsync(Guid attempt, bool protect, CancellationToken token);
    Task<DeploymentView> PauseAsync(Guid id, long revision, string reason, CancellationToken token);
    Task<DeploymentView> ResumeAsync(Guid id, long revision, Guid actor, IReadOnlyList<FailureReview> reviews, CancellationToken token);
    Task<TaskWorkAuthority> ControlAsync(Guid id, long revision, Guid actor, string kind, TaskWindow? window, CancellationToken token);
    Task<TaskView> TaskControlAsync(Guid id, long revision, string action, Guid actor, string reason, string? evidence, CancellationToken token);
    Task<Guid> ClaimAsync(Guid taskId, CancellationToken token);
    Task<StartGrant> StartAsync(Guid attemptId, Guid packageId, string sha256, CancellationToken token);
    Task<StartGrant?> ExistingGrantAsync(Guid attemptId, CancellationToken token);
    Task CheckStartAsync(Guid attemptId, CancellationToken token);
    Task<ReceiptResult?> FindReceiptAsync(Guid attempt, ReceiptInput input, string digest, CancellationToken token);
    Task<ReceiptResult> ReceiveAsync(Guid attempt, ReceiptInput input, string digest, bool? reportApplied, CancellationToken token);
    Task<bool> CanApplyReportAsync(Guid attempt, CancellationToken token);
    Task<TaskWorkAuthority> CreateWorkAsync(Guid id, Guid softwareId, Guid resource, Guid actor, string kind, CancellationToken token);
    Task<TaskWorkAuthority> AuthorityAsync(Guid id, bool protect, CancellationToken token);
    Task<IReadOnlyList<TaskWorkAuthority>> ResumeDispatchesAsync(Guid deploymentId, CancellationToken token);
    Task<bool> HasDispatchAsync(Guid id, long sequence, Guid eventId, CancellationToken token);
    Task AcceptAsync(Guid id, long sequence, Guid eventId, CancellationToken token);
    Task<IReadOnlyList<Guid>> PendingAsync(int take, CancellationToken token);
    Task<TaskLease?> ClaimWorkAsync(Guid id, Guid leaseToken, CancellationToken token);
    Task<TaskWorkView> WorkAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<Guid>> NextMembersAsync(TaskLease lease, CancellationToken token);
    Task AdmitAsync(TaskLease lease, Guid instanceId, string? reason, Guid? retryOfTask, CancellationToken token);
    Task AdvanceAsync(TaskLease lease, bool authorized, bool targetAvailable, CancellationToken token);
    Task FailWorkAsync(TaskLease lease, string code, CancellationToken token);
    Task<Guid?> RetryDeploymentAsync(Guid deploymentId, CancellationToken token);
    Task<Guid?> RetrySourceAsync(Guid deploymentId, Guid instanceId, CancellationToken token);
}
public sealed record IntegrationMaterialInput([property: JsonRequired] IReadOnlyList<string> DataLocations, [property: JsonRequired] string UpdateBehavior, [property: JsonRequired] string RollbackBehavior,
    [property: JsonRequired] string RecoveryPlan, string? VerificationConclusion, [property: JsonRequired] IReadOnlyList<string> EvidenceReferences, [property: JsonRequired] string Reason);
public sealed record IntegrationMaterialView(Guid Id, Guid ReleaseId, long Revision, IReadOnlyList<string> DataLocations,
    string UpdateBehavior, string RollbackBehavior, string RecoveryPlan, string? VerificationConclusion,
    IReadOnlyList<string> EvidenceReferences, string Reason, Guid RecordedBy, DateTimeOffset RecordedAt);
public interface IIntegrationMaterials
{
    Task<IntegrationMaterialView> RecordAsync(Guid id, Guid releaseId, IntegrationMaterialInput input, Guid actor, CancellationToken token);
    Task<IReadOnlyList<IntegrationMaterialView>> GetAsync(Guid releaseId, int take, Guid? after, CancellationToken token);
    Task<IntegrationMaterialView> FindAsync(Guid id, CancellationToken token);
}
