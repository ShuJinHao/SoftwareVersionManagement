using Svm.SharedKernel.Domain;

namespace Svm.Core.Tasks;

public sealed class TargetSelection : AggregateRoot<StrongId<TargetSelection>>
{
    private TargetSelection(StrongId<TargetSelection> id) : base(id) { }
    public TargetSelection(Guid id, Guid software, Guid owner, string mode, string? filter) : base(new(id))
    { SoftwareId = software; OwnerId = owner; Mode = mode; Filter = filter; State = mode == "Filter" ? "Building" : "Draft"; }
    public Guid SoftwareId { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Mode { get; private set; } = "";
    public string? Filter { get; private set; }
    public string State { get; private set; } = "";
    public int MemberCount { get; private set; }
    public int ReceivedChunkCount { get; private set; }
    public DateTimeOffset? SnapshotAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public void Append(int added, bool chunk) { MemberCount += added; if (chunk) ReceivedChunkCount++; Revision++; }
    public void Seal(DateTimeOffset at) { State = "Sealed"; SnapshotAt = at; Revision++; }
    public void Fail() { State = "Failed"; Revision++; }
}
public sealed record TargetMember(StrongId<TargetSelection> SelectionId, Guid InstanceId);
public sealed record SelectionChunk(StrongId<TargetSelection> SelectionId, int Number, string Digest, int MemberCount, int ReceivedChunkCount, long Revision);
public sealed class Deployment : AggregateRoot<StrongId<Deployment>>
{
    private Deployment(StrongId<Deployment> id) : base(id) { }
    public Deployment(Guid id, Guid software, Guid selection, Guid release, Guid subject, string reason,
        DateTimeOffset notBefore, DateTimeOffset latestStart, int batchSize, int failureLimit, int waitSeconds,
        int count, Guid? retryOf, DateTimeOffset createdAt) : base(new(id))
    { SoftwareId = software; SelectionId = new(selection); TargetReleaseId = release; CreatedBy = AuthorizationSubjectId = subject;
      Reason = reason; NotBefore = notBefore; LatestStart = latestStart; BatchSize = batchSize; FailureLimit = failureLimit;
      CreatedAt = createdAt; ResultWaitSeconds = waitSeconds; SelectedCount = count; RetryOfDeploymentId = retryOf; }
    public Guid SoftwareId { get; private set; }
    public StrongId<TargetSelection> SelectionId { get; private set; }
    public Guid TargetReleaseId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public bool IsPrepared { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid AuthorizationSubjectId { get; private set; }
    public Guid? RetryOfDeploymentId { get; private set; }
    public string Reason { get; private set; } = "";
    public string State { get; private set; } = "Preparing";
    public DateTimeOffset NotBefore { get; private set; }
    public DateTimeOffset LatestStart { get; private set; }
    public int BatchSize { get; private set; }
    public int FailureLimit { get; private set; }
    public int ResultWaitSeconds { get; private set; }
    public int SelectedCount { get; private set; }
    public int ProcessedCount { get; private set; }
    public int AcceptedCount { get; private set; }
    public int RejectedCount { get; private set; }
    public string PauseCodes { get; private set; } = "";
    public bool ControlPending { get; private set; }
    public long Revision { get; private set; } = 1;
    public bool Ended => State is "Rejected" or "Canceled" or "Completed";
    public void Admit(bool accepted) { ProcessedCount++; if (accepted) AcceptedCount++; else RejectedCount++; Revision++; }
    public void Prepared() { IsPrepared = true; State = AcceptedCount == 0 ? "Rejected" : PauseCodes.Length == 0 ? "Running" : "Paused"; Revision++; }
    public void Pause(string code)
    { if (Ended || PauseCodes.Split(',',StringSplitOptions.RemoveEmptyEntries).Contains(code,StringComparer.Ordinal)) return;
        PauseCodes = string.Join(',', PauseCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Append(code));
      if (State != "Preparing") State = "Paused"; Revision++; }
    public void Resume(Guid subject) { AuthorizationSubjectId = subject; PauseCodes = ""; State = IsPrepared ? "Running" : "Preparing"; Revision++; }
    public void BeginControl() { ControlPending = true; Pause("ManualPause"); }
    public void FinishControl(string kind, DateTimeOffset? start, DateTimeOffset? end)
    { ControlPending = false; if (kind == "Reschedule") { NotBefore = start!.Value; LatestStart = end!.Value; }
      if (kind == "Cancel") State = "Canceled"; Revision++; }
    public void Complete() { State = "Completed"; Revision++; }
}
public sealed record AdmissionItem(StrongId<Deployment> DeploymentId, Guid InstanceId, string Decision, string? ReasonCode, Guid? TaskId);
public sealed class TaskBatch : AggregateRoot<StrongId<TaskBatch>>
{
    private TaskBatch(StrongId<TaskBatch> id) : base(id) { }
    public TaskBatch(Guid id, Guid deployment, int ordinal) : base(new(id)) { DeploymentId = new(deployment); Ordinal = ordinal; }
    public StrongId<Deployment> DeploymentId { get; private set; }
    public int Ordinal { get; private set; }
    public string State { get; private set; } = "Pending";
    public DateTimeOffset? OpenedAt { get; private set; }
    public int TaskCount { get; private set; }
    public long FailureRevision { get; private set; }
    public long ReviewedFailureRevision { get; private set; }
    public long Revision { get; private set; } = 1;
    public void Add() { TaskCount++; Revision++; }
    public void Open(DateTimeOffset at) { if (State != "Pending") throw new InvalidOperationException(); State = "Open"; OpenedAt = at; Revision++; }
    public void Close() { State = "Closed"; Revision++; }
    public void Failed() { FailureRevision++; Revision++; }
    public void Review() { ReviewedFailureRevision = FailureRevision; Revision++; }
}
public sealed class InstanceTask : AggregateRoot<StrongId<InstanceTask>>
{
    private InstanceTask(StrongId<InstanceTask> id) : base(id) { }
    public InstanceTask(Guid id, Guid deployment, Guid instance, Guid release, DateTimeOffset start,
        DateTimeOffset end, Guid? retryOf, DateTimeOffset createdAt) : base(new(id))
    { CreatedAt = createdAt; DeploymentId = new(deployment); InstanceId = instance; TargetReleaseId = release; NotBefore = start; LatestStart = end; RetryOfTaskId = retryOf; }
    public StrongId<Deployment> DeploymentId { get; private set; }
    public Guid InstanceId { get; private set; }
    public Guid TargetReleaseId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public StrongId<TaskBatch>? BatchId { get; private set; }
    public string State { get; private set; } = "Queued";
    public DateTimeOffset NotBefore { get; private set; }
    public DateTimeOffset LatestStart { get; private set; }
    public bool IsDeferred { get; private set; }
    public Guid? AttemptId { get; private set; }
    public DateTimeOffset? ResponseDeadlineAt { get; private set; }
    public string? LastReportedProgress { get; private set; }
    public long LastProgressSequence { get; private set; }
    public string? TerminalResult { get; private set; }
    public Guid? RetryOfTaskId { get; private set; }
    public Guid? ClosedBy { get; private set; }
    public string? ClosureReason { get; private set; }
    public string? OnsiteEvidence { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public bool Ended => State is "Succeeded" or "Failed" or "Canceled" or "ClosedUnknown";
    public void Assign(Guid batch) { BatchId = new(batch); Revision++; }
    public void MakeAvailable(DateTimeOffset opened, int seconds)
    { if (State == "Queued") State = "Available"; ResponseDeadlineAt = (opened > NotBefore ? opened : NotBefore).AddSeconds(seconds); }
    public void Claim(Guid attempt) { AttemptId = attempt; State = "Preparing"; Revision++; }
    public void Start() { State = "AwaitingResult"; Revision++; }
    public void Defer(bool value) { IsDeferred = value; Revision++; }
    public void Reschedule(DateTimeOffset start, DateTimeOffset end, DateTimeOffset? opened, int seconds)
    { NotBefore = start; LatestStart = end; if (opened is { } at) ResponseDeadlineAt = (at > start ? at : start).AddSeconds(seconds); Revision++; }
    public void Progress(string progress, long sequence) { LastReportedProgress = progress; LastProgressSequence = sequence; Revision++; }
    public void Terminal(string result)
    { TerminalResult = result; State = result == "NotStarted" ? "Canceled" : result; IsDeferred = false; Revision++; }
    public void CloseUnknown(Guid subject, string reason, string evidence, DateTimeOffset at)
    { ClosedBy = subject; ClosureReason = reason; OnsiteEvidence = evidence; ClosedAt = at; TerminalResult = "Unknown";
      State = "ClosedUnknown"; IsDeferred = false; Revision++; }
}
public sealed class ExecutionAttempt(Guid id, StrongId<InstanceTask> taskId)
{
    public Guid Id { get; private set; } = id;
    public StrongId<InstanceTask> TaskId { get; private set; } = taskId;
    public Guid? PackageId { get; private set; }
    public string? Sha256 { get; private set; }
    public DateTimeOffset? StartAuthorizedAt { get; private set; }
    public DateTimeOffset? MustBeginBefore { get; private set; }
    public void Authorize(Guid packageId, string sha256, DateTimeOffset at, DateTimeOffset before)
    { PackageId = packageId; Sha256 = sha256; StartAuthorizedAt = at; MustBeginBefore = before; }
}
public sealed record TaskReceipt(Guid Id, Guid AttemptId, Guid EventId, long Sequence, string Kind, string? Progress,
    string? Result, string? FailureCode, string? Detail, string Digest, DateTimeOffset ReceivedAt, bool Applied,
    bool LateAfterClosure, string TaskState, bool? StateReportApplied, string? StateReportJson);
public sealed record InstanceExecutionGuard(Guid InstanceId, StrongId<InstanceTask> TaskId);
public sealed class TaskWork : AggregateRoot<StrongId<TaskWork>>
{
    private TaskWork(StrongId<TaskWork> id) : base(id) { }
    public TaskWork(Guid id, Guid software, Guid resource, Guid actor, string kind, bool local = false) : base(new(id))
    { SoftwareId = software; ResourceId = resource; InitiatorId = actor; Kind = kind; Accepted = local; DispatchSequence = local ? 0 : 1;
      DispatchEventId = local ? Guid.Empty : Guid.NewGuid(); Stage = kind == "Deployment" ? "Admission" : kind; }
    public Guid SoftwareId { get; private set; }
    public Guid ResourceId { get; private set; }
    public Guid InitiatorId { get; private set; }
    public string Kind { get; private set; } = "";
    public string State { get; private set; } = "Pending";
    public string Stage { get; private set; } = "";
    public bool Accepted { get; private set; }
    public long DispatchSequence { get; private set; }
    public Guid DispatchEventId { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public long LeaseGeneration { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public Guid? Cursor { get; private set; }
    public int ProcessedItems { get; private set; }
    public string? LastErrorCode { get; private set; }
    public DateTimeOffset? NewNotBefore { get; private set; }
    public DateTimeOffset? NewLatestStart { get; private set; }
    public DateTimeOffset NextScanAt { get; private set; } = DateTimeOffset.UnixEpoch;
    public long Revision { get; private set; } = 1;
    public void Window(DateTimeOffset start, DateTimeOffset end) { NewNotBefore = start; NewLatestStart = end; }
    public void Accept() { if (!Accepted) { Accepted = true; Revision++; } }
    public bool Claim(Guid token, DateTimeOffset now, int seconds)
    { if (!Accepted || State is "Completed" or "Failed" || LeaseUntil > now) return false;
      LeaseToken = token; LeaseGeneration++; LeaseUntil = now.AddSeconds(seconds); State = "Running"; Revision++; return true; }
    public bool Owns(Guid token, long generation, DateTimeOffset now) => State == "Running" && LeaseToken == token && LeaseGeneration == generation && LeaseUntil > now;
    public void Advance(Guid cursor) { Cursor = cursor; ProcessedItems++; Revision++; }
    public void NextStage(string stage) { Stage = stage; Cursor = null; ProcessedItems = 0; Revision++; }
    public void Yield(DateTimeOffset next) { NextScanAt = next; State = "Pending"; LeaseUntil = null; LeaseToken = null; Revision++; }
    public void Complete() { State = "Completed"; LeaseUntil = null; Revision++; }
    public void Resume(Guid actor)
    { InitiatorId = actor; if (!Accepted) { DispatchSequence++; DispatchEventId = Guid.NewGuid(); }
      State = "Pending"; LeaseUntil = null; LeaseToken = null; NextScanAt = DateTimeOffset.UnixEpoch; LastErrorCode = null; Revision++; }
    public void Fail(string code) { State = "Failed"; LeaseUntil = null; LastErrorCode = code; Revision++; }
}
public sealed record TaskDispatch(Guid Id, StrongId<TaskWork> WorkId, long Sequence, string Kind);
public sealed record TaskControlItem(StrongId<TaskWork> WorkId, StrongId<InstanceTask> TaskId, string Outcome, string? ReasonCode);
public sealed record BatchCounts(int Unfinished, int Deferred, int Failed, int TimedOut, DateTimeOffset? EarliestDeadline);
public sealed record DeploymentCounts(int Succeeded, int Failed, int Canceled, int Unknown, int Unfinished);
public interface ITaskRepository
{
    Task<TargetSelection?> SelectionAsync(Guid id, bool protect, CancellationToken token);
    Task<SelectionChunk?> ChunkAsync(Guid selection, int number, CancellationToken token);
    Task<int> AppendMembersAsync(Guid selection, IReadOnlyList<Guid> ids, CancellationToken token);
    Task<IReadOnlyList<Guid>> MembersAsync(Guid selection, Guid? after, int take, CancellationToken token);
    Task<Deployment?> DeploymentAsync(Guid id, bool protect, CancellationToken token);
    Task<TaskBatch?> BatchAsync(Guid id, bool protect, CancellationToken token);
    Task<TaskBatch?> LastBatchAsync(Guid deployment, CancellationToken token);
    Task<IReadOnlyList<TaskBatch>> BatchesAsync(Guid deployment, bool protect, CancellationToken token);
    Task<InstanceTask?> TaskAsync(Guid id, bool protect, CancellationToken token);
    Task<InstanceTask?> RetryAsync(Guid deployment, Guid instance, CancellationToken token);
    Task<IReadOnlyList<InstanceTask>> TasksAsync(Guid deployment, Guid? after, int take, bool unbatched, bool protect, CancellationToken token);
    Task<ExecutionAttempt?> AttemptAsync(Guid id, bool protect, CancellationToken token);
    Task<TaskReceipt?> ReceiptAsync(Guid attempt, Guid eventId, long sequence, CancellationToken token);
    Task<InstanceExecutionGuard?> GuardAsync(Guid instance, CancellationToken token);
    Task ReleaseGuardAsync(Guid instance, Guid task, CancellationToken token);
    Task<IReadOnlyList<TaskWork>> RecoverableAsync(Guid resource, CancellationToken token);
    Task<TaskWork?> WorkAsync(Guid id, bool protect, CancellationToken token);
    Task<IReadOnlyList<Guid>> PendingAsync(DateTimeOffset now, int take, CancellationToken token);
    Task<bool> HasDispatchAsync(Guid work, long sequence, Guid eventId, CancellationToken token);
    Task<BatchCounts> BatchCountsAsync(Guid batch, DateTimeOffset now, CancellationToken token);
    Task<DeploymentCounts> CountsAsync(Guid deployment, CancellationToken token);
    void Add(TargetSelection value);
    void Add(SelectionChunk value);
    void Add(Deployment value);
    void Add(AdmissionItem value);
    void Add(TaskBatch value);
    void Add(InstanceTask value);
    void Add(ExecutionAttempt value);
    void Add(TaskReceipt value);
    void Add(InstanceExecutionGuard value);
    void Add(TaskWork value);
    void Add(TaskDispatch value);
    void Add(TaskControlItem value);
}
