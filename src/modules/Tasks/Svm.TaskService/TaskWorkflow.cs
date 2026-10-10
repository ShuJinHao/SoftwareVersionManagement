using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Tasks;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;

namespace Svm.TaskService;

public static class TaskRegistration
{
    public static IServiceCollection AddSvmTasks(this IServiceCollection services) => services.AddScoped<ITaskWorkflow, TaskWorkflow>();
}
internal sealed class TaskWorkflow(ITaskRepository repository, IUnitOfWork unit, ITaskTime time, TaskOptions options) : ITaskWorkflow
{
    private static RequestRejectedException Reject(RequestFailure failure = RequestFailure.InvalidState) => new(failure);
    private void Write() { options.Validate(); if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
    private static void Revision(long actual, long expected) { if (actual != expected) throw Reject(RequestFailure.RevisionConflict); }
    private async Task<TargetSelection> Selection(Guid id, bool protect, CancellationToken ct) => await repository.SelectionAsync(id, protect, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
    private async Task<Deployment> Deployment(Guid id, bool protect, CancellationToken ct) => await repository.DeploymentAsync(id, protect, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
    private async Task<InstanceTask> Task(Guid id, bool protect, CancellationToken ct) => await repository.TaskAsync(id, protect, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
    private async Task<TaskWork> Work(Guid id, bool protect, CancellationToken ct) => await repository.WorkAsync(id, protect, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
    public async Task<Guid?> SoftwareForAsync(string resource, Guid id, CancellationToken ct) => resource switch
    {
        "selection" => (await repository.SelectionAsync(id, false, ct))?.SoftwareId,
        "deployment" => (await repository.DeploymentAsync(id, false, ct))?.SoftwareId,
        "work" => (await repository.WorkAsync(id, false, ct))?.SoftwareId,
        "task" => await TaskSoftware(id, ct),
        "attempt" => await AttemptSoftware(id, ct),
        _ => throw Reject(RequestFailure.ResourceNotFound)
    };
    private async Task<Guid?> TaskSoftware(Guid id, CancellationToken ct) => (await repository.TaskAsync(id, false, ct)) is { } t ?
        (await repository.DeploymentAsync(t.DeploymentId.Value, false, ct))?.SoftwareId : null;
    private async Task<Guid?> AttemptSoftware(Guid id, CancellationToken ct) => (await repository.AttemptAsync(id, false, ct)) is { } a ? await TaskSoftware(a.TaskId.Value, ct) : null;
    public async Task<Svm.Services.Contracts.Instances.InstanceFilter> FilterAsync(Guid id, CancellationToken ct) => JsonSerializer.Deserialize<Svm.Services.Contracts.Instances.InstanceFilter>((await Selection(id, false, ct)).Filter ?? throw Reject()) ?? throw Reject();
    public async Task<Guid> OwnerAsync(string resource, Guid id, CancellationToken ct) => resource == "selection" ? (await Selection(id, false, ct)).OwnerId :
        resource == "deployment" ? (await Deployment(id, false, ct)).AuthorizationSubjectId : (await Work(id, false, ct)).InitiatorId;
    public async Task<SelectionView> SelectionAsync(Guid id, bool protect, CancellationToken ct) => View(await Selection(id, protect, ct));
    public Task<SelectionView> CreateSelectionAsync(Guid id, SelectionInput input, Guid actor, CancellationToken ct)
    {
        Write(); ct.ThrowIfCancellationRequested();
        if (input.Mode is not ("Explicit" or "Filter") || input.Mode == "Filter" && (input.Filter is null || input.Filter.SoftwareId != input.SoftwareId) || input.Mode == "Explicit" && input.Filter is not null)
            throw Reject(RequestFailure.ValidationFailed);
        var s = new TargetSelection(id, input.SoftwareId, actor, input.Mode, input.Filter is null ? null : JsonSerializer.Serialize(input.Filter)); repository.Add(s);
        return System.Threading.Tasks.Task.FromResult(View(s));
    }
    public async Task<SelectionView?> FindChunkAsync(Guid id, int number, string digest, CancellationToken ct)
    { var c = await repository.ChunkAsync(id, number, ct); if (c is null) return null;
      if (c.Digest != digest) throw Reject(RequestFailure.IdempotencyConflict);
      var s = await Selection(id, false, ct); return new(id, s.SoftwareId, s.Mode, "Draft", c.MemberCount, c.ReceivedChunkCount, null, c.Revision); }
    public async Task<SelectionView> ChunkAsync(Guid id, int number, IReadOnlyList<Guid> ids, string digest, CancellationToken ct)
    {
        Write(); var s = await Selection(id, true, ct); var old = await FindChunkAsync(id, number, digest, ct); if (old is not null) return old;
        if (s.State != "Draft" || s.Mode != "Explicit" || number != s.ReceivedChunkCount || ids.Count < 1 || ids.Count > options.SelectionChunkSize || ids.Any(x => x == Guid.Empty)) throw Reject(RequestFailure.ValidationFailed);
        var added = await repository.AppendMembersAsync(id, ids.Distinct().Order().ToArray(), ct);
        if (s.MemberCount + added > options.MaxSelectionMembers) throw Reject(RequestFailure.PayloadTooLarge);
        s.Append(added, true); repository.Add(new SelectionChunk(new(id), number, digest, s.MemberCount, s.ReceivedChunkCount, s.Revision)); return View(s);
    }
    public async Task<SelectionView> SealAsync(Guid id, long revision, int chunks, int count, CancellationToken ct)
    { Write(); var s = await Selection(id, true, ct); Revision(s.Revision, revision);
      if (s.State != "Draft" || s.Mode != "Explicit" || count < 1 || s.MemberCount != count || chunks != s.ReceivedChunkCount) throw Reject();
      s.Seal(await time.NowAsync(ct)); return View(s); }
    public async Task AddSnapshotMembersAsync(Guid id, IReadOnlyList<Guid> ids, CancellationToken ct)
    { Write(); var s = await Selection(id, true, ct); if (s.State != "Building") throw Reject();
      var count = await repository.AppendMembersAsync(id, ids, ct); if (s.MemberCount + count > options.MaxSelectionMembers) throw Reject(RequestFailure.PayloadTooLarge); s.Append(count, false); }
    public async Task CompleteSnapshotAsync(Guid id, DateTimeOffset at, CancellationToken ct)
    { Write(); var s = await Selection(id, true, ct); if (s.State != "Building") throw Reject(); s.Seal(at); }
    public async Task<DeploymentView> CreateAsync(Guid id, DeploymentInput input, Guid releaseId, Guid actor, TaskWindow window, CancellationToken ct)
    {
        Write(); var s = await Selection(input.SelectionId, true, ct);
        if (input.Kind != "Update") throw Reject(RequestFailure.ValidationFailed);
        if (s.State != "Sealed" || s.SoftwareId != input.SoftwareId || s.OwnerId != actor || s.MemberCount < 1) throw Reject();
        if (window.NotBefore >= window.LatestStart || window.LatestStart <= await time.NowAsync(ct)) throw Reject(RequestFailure.ValidationFailed);
        if (input.RetryOfDeploymentId is { } old && (await Deployment(old, true, ct)).SoftwareId != input.SoftwareId) throw Reject();
        var d = new Deployment(id, input.SoftwareId, input.SelectionId, releaseId, actor, input.Reason,
            window.NotBefore, window.LatestStart, options.BatchSize!.Value, options.FailureLimit!.Value, options.ResultWaitSeconds!.Value, s.MemberCount, input.RetryOfDeploymentId, await time.NowAsync(ct));
        repository.Add(d); return await View(d, ct);
    }
    public async Task<DeploymentView> DeploymentAsync(Guid id, bool protect, CancellationToken ct) => await View(await Deployment(id, protect, ct), ct);
    public async Task<IReadOnlyList<BatchView>> BatchesAsync(Guid id, CancellationToken ct)
    { var now = await time.NowAsync(ct); var result = new List<BatchView>(); foreach (var b in await repository.BatchesAsync(id, false, ct)) result.Add(await View(b, now, ct)); return result.AsReadOnly(); }
    public async Task<TaskView> TaskAsync(Guid id, bool protect, CancellationToken ct) => await View(protect ? (await Protected(id, ct)).Task : await Task(id, false, ct), ct);
    public async Task<TaskView> AttemptTaskAsync(Guid id, bool protect, CancellationToken ct)
    { var a = await repository.AttemptAsync(id, false, ct) ?? throw Reject(RequestFailure.ResourceNotFound); return await TaskAsync(a.TaskId.Value, protect, ct); }
    public async Task<DeploymentView> PauseAsync(Guid id, long revision, string reason, CancellationToken ct)
    { Write(); var d = await Deployment(id, true, ct); Revision(d.Revision, revision); if (d.Ended) throw Reject(); d.Pause("ManualPause"); return await View(d, ct); }
    public async Task<DeploymentView> ResumeAsync(Guid id, long revision, Guid actor, IReadOnlyList<FailureReview> reviews, CancellationToken ct)
    {
        Write(); var d = await Deployment(id, true, ct); Revision(d.Revision, revision); var now = await time.NowAsync(ct);
        if (d.State is not ("Paused" or "Preparing") || d.PauseCodes.Length == 0 ) throw Reject();
        if (d.ControlPending)
        { var recovery = await repository.RecoverableAsync(id,ct); if (!recovery.Any(w=>w.Kind is "Cancel" or "Reschedule")) throw Reject();
          foreach(var w in recovery.Where(w=>w.Kind is "Cancel" or "Reschedule")) w.Resume(actor); return await View(d,ct); }
        if(d.LatestStart <= now) throw Reject();
        var batches = await repository.BatchesAsync(id, true, ct);
        if (reviews.Select(x => x.BatchId).Distinct().Count() != reviews.Count || reviews.Any(x => !batches.Any(b => b.Id.Value == x.BatchId))) throw Reject(RequestFailure.ValidationFailed);
        foreach (var b in batches)
        {
            var counts = await repository.BatchCountsAsync(b.Id.Value, now, ct);
            if (reviews.Any(x => x.BatchId == b.Id.Value && x.FailureRevision != b.FailureRevision)) throw Reject(RequestFailure.RevisionConflict);
            if (counts.TimedOut != 0 || b.FailureRevision > b.ReviewedFailureRevision && !reviews.Any(x => x.BatchId == b.Id.Value && x.FailureRevision == b.FailureRevision)) throw Reject();
        }
        foreach (var b in batches) b.Review(); d.Resume(actor);
        foreach(var w in await repository.RecoverableAsync(id,ct)) w.Resume(actor); return await View(d, ct);
    }
    public async Task<TaskWorkAuthority> ControlAsync(Guid id, long revision, Guid actor, string kind, TaskWindow? window, CancellationToken ct)
    {
        Write(); var d = await Deployment(id, true, ct); Revision(d.Revision, revision);
        if (d.Ended || d.ControlPending || kind is not ("Reschedule" or "Cancel")) throw Reject();
        if (kind == "Reschedule" && (window is null || window.NotBefore >= window.LatestStart || window.LatestStart <= await time.NowAsync(ct))) throw Reject(RequestFailure.ValidationFailed);
        d.BeginControl(); var w = new TaskWork(Guid.NewGuid(), d.SoftwareId, id, actor, kind);
        if (window is not null) w.Window(window.NotBefore, window.LatestStart); repository.Add(w); repository.Add(new TaskDispatch(w.DispatchEventId, w.Id, w.DispatchSequence, kind)); return Authority(w);
    }
    public async Task<TaskView> TaskControlAsync(Guid id, long revision, string action, Guid actor, string reason, string? evidence, CancellationToken ct)
    {
        Write(); var (t, d, b, a) = await Protected(id, ct); Revision(t.Revision, revision);
        if (t.Ended || d.ControlPending) throw Reject();
        if (action == "Defer") { t.Defer(true); }
        else if (action == "Restore") { if (b?.State != "Open" || !t.IsDeferred) throw Reject(); t.Defer(false); }
        else if (action == "Cancel") { if (a?.StartAuthorizedAt is not null) throw Reject(); t.Terminal("NotStarted"); await repository.ReleaseGuardAsync(t.InstanceId, id, ct); }
        else if (action == "CloseUnknown")
        { if (t.State != "AwaitingResult" || t.ResponseDeadlineAt is null || t.ResponseDeadlineAt >= await time.NowAsync(ct) || string.IsNullOrWhiteSpace(evidence)) throw Reject();
          t.CloseUnknown(actor, reason, evidence, await time.NowAsync(ct)); await repository.ReleaseGuardAsync(t.InstanceId, id, ct); }
        else throw Reject(RequestFailure.ValidationFailed);
        return await View(t, ct);
    }
    public async Task<Guid> ClaimAsync(Guid id, CancellationToken ct)
    {
        Write(); var (t, d, b, a) = await Protected(id, ct); if (a is not null) return a.Id;
        var now = await time.NowAsync(ct); if (t.Ended || t.IsDeferred || b?.State != "Open" || d.ControlPending || BlocksStart(d) || now >= t.LatestStart || t.State is not ("Queued" or "Available")) throw Reject();
        var attempt = new ExecutionAttempt(Guid.NewGuid(), new(id)); t.Claim(attempt.Id); repository.Add(attempt); return attempt.Id;
    }
    public async Task CheckStartAsync(Guid id, CancellationToken ct)
    { Write(); var raw = await repository.AttemptAsync(id, false, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
      var (t, d, b, a) = await Protected(raw.TaskId.Value, ct); if (a?.StartAuthorizedAt is not null) return;
      var now = await time.NowAsync(ct);
      if (a?.Id != id || t.State != "Preparing" || t.IsDeferred || b?.State != "Open" || d.ControlPending || BlocksStart(d) || now < t.NotBefore || now >= t.LatestStart) throw Reject(); }
    public async Task<StartGrant?> ExistingGrantAsync(Guid id, CancellationToken ct)
    { var a = await repository.AttemptAsync(id, false, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
      if (a.StartAuthorizedAt is null) return null; var t = await Task(a.TaskId.Value, false, ct); return Grant(t, a); }
    public async Task<StartGrant> StartAsync(Guid id, Guid packageId, string sha256, CancellationToken ct)
    { await CheckStartAsync(id, ct); var a = await repository.AttemptAsync(id, true, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
      var t = await Task(a.TaskId.Value, true, ct); if (a.StartAuthorizedAt is not null) return Grant(t, a);
      a.Authorize(packageId, sha256, await time.NowAsync(ct), t.LatestStart); t.Start(); return Grant(t, a); }
    public async Task<ReceiptResult?> FindReceiptAsync(Guid id, ReceiptInput input, string digest, CancellationToken ct)
    { var r = await repository.ReceiptAsync(id, input.EventId, input.Sequence, ct); if (r is null) return null;
      if (r.EventId != input.EventId || r.Sequence != input.Sequence || r.Digest != digest) throw Reject(RequestFailure.ReceiptConflict);
      var a = await repository.AttemptAsync(id, false, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
      return new(r.Id, a.TaskId.Value, id, r.Applied, r.LateAfterClosure, r.TaskState, r.StateReportApplied); }
    public async Task<bool> CanApplyReportAsync(Guid id, CancellationToken ct)
    { var a = await repository.AttemptAsync(id, false, ct) ?? throw Reject(RequestFailure.ResourceNotFound); return !(await Protected(a.TaskId.Value, ct)).Task.Ended; }
    public async Task<ReceiptResult> ReceiveAsync(Guid id, ReceiptInput input, string digest, bool? reportApplied, CancellationToken ct)
    {
        Write(); var raw = await repository.AttemptAsync(id, false, ct) ?? throw Reject(RequestFailure.ResourceNotFound);
        var (t, _, batch, attempt) = await Protected(raw.TaskId.Value, ct); var old = await FindReceiptAsync(id, input, digest, ct); if (old is not null) return old;
        var late = t.State == "ClosedUnknown"; var applied = false;
        if (!late && t.State is "Succeeded" or "Failed" && input.Kind == "Terminal") throw Reject(RequestFailure.ReceiptConflict);
        if (!t.Ended)
        {
            if ((input.Progress == "Installing" || input.Result == "Succeeded") && attempt?.StartAuthorizedAt is null) throw Reject();
            if (input.Kind == "Terminal") { t.Terminal(input.Result!); applied = true;
                if (input.Result == "Failed") batch?.Failed(); await repository.ReleaseGuardAsync(t.InstanceId, t.Id.Value, ct); }
            else if (input.Sequence > t.LastProgressSequence && Rank(input.Progress) >= Rank(t.LastReportedProgress)) { t.Progress(input.Progress!, input.Sequence); applied = true; }
        }
        var r = new TaskReceipt(Guid.NewGuid(), id, input.EventId, input.Sequence, input.Kind, input.Progress, input.Result,
            input.FailureCode, input.Detail, digest, await time.NowAsync(ct), applied, late, t.State, t.Ended && !applied ? false : reportApplied, input.StateReport is null ? null : JsonSerializer.Serialize(input.StateReport));
        repository.Add(r); return new(r.Id, t.Id.Value, id, applied, late, t.State, r.StateReportApplied);
    }
    public async Task<TaskWorkAuthority> CreateWorkAsync(Guid id, Guid software, Guid resource, Guid actor, string kind, CancellationToken ct)
    { Write(); ct.ThrowIfCancellationRequested(); var w = new TaskWork(id, software, resource, actor, kind, kind == "Schedule"); repository.Add(w);
      if (kind != "Schedule") repository.Add(new TaskDispatch(w.DispatchEventId, new(id), w.DispatchSequence, kind)); return await System.Threading.Tasks.Task.FromResult(Authority(w)); }
    public async Task<TaskWorkAuthority> AuthorityAsync(Guid id, bool protect, CancellationToken ct)
    { var w = await Work(id, protect, ct); var authority = Authority(w);
      return w.Kind is "Deployment" or "Schedule" ? authority with { InitiatorId = (await Deployment(w.ResourceId, false, ct)).AuthorizationSubjectId } : authority; }
    public async Task<IReadOnlyList<TaskWorkAuthority>> ResumeDispatchesAsync(Guid id,CancellationToken ct)
    { Write(); var result = new List<TaskWorkAuthority>(); foreach(var w in await repository.RecoverableAsync(id,ct))
      if(!w.Accepted && !await repository.HasDispatchAsync(w.Id.Value,w.DispatchSequence,w.DispatchEventId,ct))
      { repository.Add(new TaskDispatch(w.DispatchEventId,w.Id,w.DispatchSequence,w.Kind)); result.Add(Authority(w)); } return result; }
    public Task<bool> HasDispatchAsync(Guid id, long sequence, Guid eventId, CancellationToken ct) => repository.HasDispatchAsync(id, sequence, eventId, ct);
    public async Task AcceptAsync(Guid id, long sequence, Guid eventId, CancellationToken ct)
    { Write(); var w = await Work(id, true, ct); if (w.DispatchSequence != sequence || w.DispatchEventId != eventId) return; w.Accept(); }
    public async Task<IReadOnlyList<Guid>> PendingAsync(int take, CancellationToken ct) => await repository.PendingAsync(await time.NowAsync(ct), take, ct);
    public async Task<TaskLease?> ClaimWorkAsync(Guid id, Guid token, CancellationToken ct)
    { Write(); var w = await Work(id, true, ct); return w.Claim(token, await time.NowAsync(ct), options.LeaseSeconds!.Value) ? new(Authority(w), token, w.LeaseGeneration) : null; }
    public async Task<TaskWorkView> WorkAsync(Guid id, CancellationToken ct)
    { var w = await Work(id, false, ct); return new(id, w.SoftwareId, w.Kind, w.State, w.Stage, w.Cursor, w.ProcessedItems, w.LastErrorCode, w.Revision); }
    public async Task<IReadOnlyList<Guid>> NextMembersAsync(TaskLease lease, CancellationToken ct)
    { var w = await Owned(lease, ct); var d = await Deployment(w.ResourceId, true, ct);
      return w.Kind == "Deployment" && w.Stage == "Admission" && !d.ControlPending && !d.Ended ?
          await repository.MembersAsync(d.SelectionId.Value, w.Cursor, options.SelectionChunkSize!.Value, ct) : []; }
    public async Task<Guid?> RetryDeploymentAsync(Guid id, CancellationToken ct) => (await Deployment(id, false, ct)).RetryOfDeploymentId;
    public async Task<Guid?> RetrySourceAsync(Guid deployment, Guid instance, CancellationToken ct)
    { var t = await repository.RetryAsync(deployment, instance, ct); if (t is null || !t.Ended) throw Reject(RequestFailure.RetrySourceInvalid); return t.Id.Value; }
    public async Task AdmitAsync(TaskLease lease, Guid instance, string? reason, Guid? retryOf, CancellationToken ct)
    {
        Write(); var w = await Owned(lease, ct); var d = await Deployment(w.ResourceId, true, ct);
        if (w.Stage != "Admission" || d.Ended || d.ControlPending || d.PauseCodes.Contains("AuthorizationChanged", StringComparison.Ordinal)) throw Reject();
        if (await repository.GuardAsync(instance, ct) is not null) reason = "INSTANCE_BUSY";
        Guid? taskId = null;
        if (reason is null) { taskId = Guid.NewGuid(); repository.Add(new InstanceTask(taskId.Value, d.Id.Value, instance, d.TargetReleaseId, d.NotBefore, d.LatestStart, retryOf, await time.NowAsync(ct))); repository.Add(new InstanceExecutionGuard(instance, new(taskId.Value))); }
        repository.Add(new AdmissionItem(d.Id, instance, reason is null ? "Accepted" : "Rejected", reason, taskId)); d.Admit(reason is null); w.Advance(instance);
    }
    public async Task AdvanceAsync(TaskLease lease, bool authorized, bool targetAvailable, CancellationToken ct)
    {
        Write(); var w = await Owned(lease, ct);
        if (w.Kind == "TargetSelection") { var s = await Selection(w.ResourceId, true, ct); if (s.State == "Sealed") w.Complete(); else { s.Fail(); w.Fail("SNAPSHOT_FAILED"); } return; }
        var d = await Deployment(w.ResourceId, true, ct); var now = await time.NowAsync(ct);
        if (!authorized) { d.Pause("AuthorizationChanged"); w.Yield(now.AddSeconds(options.PollRetrySeconds!.Value)); return; }
        if (w.Kind == "Schedule") { await Schedule(d, now, targetAvailable, ct); if (d.Ended) w.Complete(); else w.Yield(now.AddSeconds(options.PollRetrySeconds!.Value)); return; }
        if (w.Kind == "Deployment")
        {
            if (d.ControlPending || d.PauseCodes.Length != 0) { w.Yield(now.AddSeconds(options.PollRetrySeconds!.Value)); return; }
            if (d.Ended) { w.Complete(); return; }
            if (!targetAvailable) { d.Pause("TargetUnavailable"); w.Yield(now.AddSeconds(options.PollRetrySeconds!.Value)); return; }
            if (w.Stage == "Admission")
            { if (d.ProcessedCount != d.SelectedCount) { w.Yield(now.AddSeconds(options.PollRetrySeconds!.Value)); return; } w.NextStage("Compile"); w.Yield(now); return; }
            var tasks = await repository.TasksAsync(d.Id.Value, null, options.SelectionChunkSize!.Value, true, true, ct);
            var last = await repository.LastBatchAsync(d.Id.Value, ct); var batches = last is null ? new List<TaskBatch>() : new List<TaskBatch> { last };
            foreach (var t in tasks)
            {
                var ordinal = w.ProcessedItems / d.BatchSize + 1; var b = batches.SingleOrDefault(x => x.Ordinal == ordinal);
                if (b is null) { b = new TaskBatch(Guid.NewGuid(), d.Id.Value, ordinal); repository.Add(b); batches.Add(b); }
                t.Assign(b.Id.Value); b.Add(); w.Advance(t.Id.Value);
            }
            if (tasks.Count == 0) { d.Prepared(); w.Complete(); if (d.AcceptedCount > 0) repository.Add(new TaskWork(Guid.NewGuid(), d.SoftwareId, d.Id.Value, d.AuthorizationSubjectId, "Schedule", true)); }
            else w.Yield(now.AddSeconds(options.PollRetrySeconds!.Value)); return;
        }
        var items = await repository.TasksAsync(d.Id.Value, w.Cursor, options.SelectionChunkSize!.Value, false, false, ct);
        var touched = new Dictionary<Guid, TaskBatch>();
        foreach (var candidate in items)
        {
            var (t, _, b, a) = await Protected(candidate.Id.Value, ct);
            if (b is not null) touched[b.Id.Value] = b;
            var skipped = t.Ended ? "ALREADY_TERMINAL" : a?.StartAuthorizedAt is not null ? "ALREADY_AUTHORIZED" : null;
            if (skipped is null)
            {
                if (w.Kind == "Cancel") { t.Terminal("NotStarted"); await repository.ReleaseGuardAsync(t.InstanceId, t.Id.Value, ct); }
                else { t.Reschedule(w.NewNotBefore!.Value, w.NewLatestStart!.Value, b?.OpenedAt, d.ResultWaitSeconds); }
            }
            repository.Add(new TaskControlItem(w.Id, t.Id, skipped is null ? "Applied" : "Skipped", skipped)); w.Advance(t.Id.Value);
        }
        foreach (var b in touched.Values)
            if (w.Kind == "Cancel" && b.State == "Pending" && (await repository.BatchCountsAsync(b.Id.Value, now, ct)).Unfinished == 0) b.Close();
        if (items.Count == 0) { d.FinishControl(w.Kind, w.NewNotBefore, w.NewLatestStart); w.Complete(); } else w.Yield(now.AddSeconds(options.PollRetrySeconds!.Value));
    }
    private async Task Schedule(Deployment d, DateTimeOffset now, bool available, CancellationToken ct)
    {
        if (d.State == "Preparing" || d.ControlPending) return;
        var batches = await repository.BatchesAsync(d.Id.Value, true, ct); var all = await repository.CountsAsync(d.Id.Value, ct);
        if (all.Unfinished > 0)
        { if (!available) d.Pause("TargetUnavailable"); if (now >= d.LatestStart) d.Pause("WindowExpired"); }
        var closed = false;
        foreach (var b in batches)
        {
            var counts = await repository.BatchCountsAsync(b.Id.Value, now, ct);
            if (counts.Failed >= d.FailureLimit && b.FailureRevision > b.ReviewedFailureRevision) d.Pause("FailureLimit");
            if (counts.TimedOut > 0) d.Pause("ReceiptTimeout");
            if (b.State == "Open" && (counts.Failed < d.FailureLimit || b.FailureRevision <= b.ReviewedFailureRevision) && counts.TimedOut == 0 && (counts.Unfinished == 0 || counts.Unfinished == counts.Deferred)) { b.Close(); closed = true; if (counts.Deferred > 0) d.Pause("DeferredOutstanding"); }
        }
        if (all.Unfinished == 0 && !batches.Any(x => x.State == "Open")) { if (!d.Ended) d.Complete(); return; }
        if (closed || d.Ended || d.PauseCodes.Length > 0 || batches.Any(x => x.State == "Open")) return;
        var next = batches.Where(x => x.State == "Pending").OrderBy(x => x.Ordinal).FirstOrDefault(); if (next is null) { d.Pause("DeferredOutstanding"); return; }
        next.Open(now);
        // Availability is projected from the open batch. Deadlines are fixed when a task is read/claimed,
        // and computed identically by query/scheduler without a large all-task write transaction.
    }
    public async Task FailWorkAsync(TaskLease lease, string code, CancellationToken ct)
    { Write(); var w = await Owned(lease, ct); w.Fail(code); if (w.Kind == "TargetSelection") (await Selection(w.ResourceId, true, ct)).Fail();
      else (await Deployment(w.ResourceId, true, ct)).Pause("ControlFailed"); }
    private async Task<TaskWork> Owned(TaskLease lease, CancellationToken ct)
    { var w = await Work(lease.Work.WorkId, true, ct); if (w.DispatchSequence != lease.Work.DispatchSequence || !w.Owns(lease.Token, lease.Generation, await time.NowAsync(ct))) throw Reject(); return w; }
    private async Task<(InstanceTask Task, Deployment Deployment, TaskBatch? Batch, ExecutionAttempt? Attempt)> Protected(Guid id, CancellationToken ct)
    {
        var raw = await Task(id, false, ct); var d = await Deployment(raw.DeploymentId.Value, true, ct);
        var b = raw.BatchId is { } batch ? await repository.BatchAsync(batch.Value,true,ct) : null; await repository.GuardAsync(raw.InstanceId, ct);
        var t = await Task(id, true, ct);
        if (!t.Ended && b?.OpenedAt is { } opened && t.ResponseDeadlineAt is null) t.MakeAvailable(opened, d.ResultWaitSeconds);
        return (t, d, b, t.AttemptId is { } a ? await repository.AttemptAsync(a, true, ct) : null);
    }
    private static bool BlocksStart(Deployment d) => d.Ended || d.PauseCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(x => x is not ("FailureLimit" or "ReceiptTimeout"));
    private static int Rank(string? p) => p switch { "Downloading" => 1, "ReadyToInstall" => 2, "Installing" => 3, _ => 0 };
    private static SelectionView View(TargetSelection s) => new(s.Id.Value, s.SoftwareId, s.Mode, s.State, s.MemberCount, s.ReceivedChunkCount, s.SnapshotAt, s.Revision);
    private async Task<DeploymentView> View(Deployment d, CancellationToken ct)
    { var c = await repository.CountsAsync(d.Id.Value, ct); return new(d.Id.Value, d.SoftwareId, d.TargetReleaseId, "Update", d.State, d.AuthorizationSubjectId,
        new(d.NotBefore, d.LatestStart), new(d.BatchSize, d.FailureLimit, d.ResultWaitSeconds), d.SelectionId.Value, d.SelectedCount, d.ProcessedCount, d.AcceptedCount, d.RejectedCount,
        new(c.Succeeded, c.Failed, c.Canceled, c.Unknown, c.Unfinished), d.PauseCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => new PauseReason(x, x is "FailureLimit" or "ReceiptTimeout" ? "FutureBatches" : "NotStarted")).ToArray(), d.ControlPending, d.Revision); }
    private async Task<BatchView> View(TaskBatch b, DateTimeOffset now, CancellationToken ct)
    { var c = await repository.BatchCountsAsync(b.Id.Value, now, ct); return new(b.Id.Value, b.Ordinal, b.State, b.OpenedAt, b.TaskCount, c.Deferred, c.Failed, b.FailureRevision, b.ReviewedFailureRevision, c.TimedOut, c.EarliestDeadline, b.Revision); }
    private async Task<TaskView> View(InstanceTask t, CancellationToken ct)
    { var a = t.AttemptId is { } id ? await repository.AttemptAsync(id, false, ct) : null;
      var d = await Deployment(t.DeploymentId.Value, false, ct); var b = t.BatchId is { } batch ? await repository.BatchAsync(batch.Value, false, ct) : null;
      var deadline = t.ResponseDeadlineAt ?? (b?.OpenedAt is { } at ? (at > t.NotBefore ? at : t.NotBefore).AddSeconds(d.ResultWaitSeconds) : (DateTimeOffset?)null);
      var now = await time.NowAsync(ct);
      var wait = t.Ended ? null : t.IsDeferred || d.ControlPending ? "Paused" : deadline < now ? "ReceiptTimeout" : a?.StartAuthorizedAt is null && now >= t.LatestStart ? "WindowMissed" : BlocksStart(d) ? "Paused" : null;
      return new(t.Id.Value, t.DeploymentId.Value, t.BatchId?.Value, t.InstanceId, t.TargetReleaseId, t.State == "Queued" && b?.State == "Open" ? "Available" : t.State,
        new(t.NotBefore, t.LatestStart), wait,
        t.IsDeferred, deadline, t.AttemptId, a?.StartAuthorizedAt, a?.MustBeginBefore, t.LastReportedProgress, t.TerminalResult,
        t.ClosedAt is { } closed ? new(t.ClosedBy!.Value, t.ClosureReason!, t.OnsiteEvidence!, closed) : null, t.RetryOfTaskId, t.Revision); }
    private static StartGrant Grant(InstanceTask t, ExecutionAttempt a) => new(t.Id.Value, a.Id, t.TargetReleaseId, a.PackageId!.Value, a.Sha256!, a.StartAuthorizedAt!.Value, a.MustBeginBefore!.Value);
    private static TaskWorkAuthority Authority(TaskWork w) => new(w.Id.Value, w.SoftwareId, w.ResourceId, w.InitiatorId, w.Kind, w.DispatchSequence, w.DispatchEventId, w.Accepted, w.State, w.Stage, w.LeaseToken, w.LeaseGeneration, w.LeaseUntil);
}
