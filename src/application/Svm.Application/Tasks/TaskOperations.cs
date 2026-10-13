using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Messaging.V1;

namespace Svm.Application.Tasks;

internal sealed class TaskOperations(ITaskWorkflow tasks, IInstanceTaskFacts facts, IManagedInstances instances,
    IReleases releases, IPackages packages, IIntegrationMaterials materials, IPersonnelWorkAuthorization authorization,
    ITaskTime time, TaskOptions options, SiteCatalogOptions site, ICallContext calls,
    IUnitOfWork unit, IAuditWriter audit)
{
    private Guid Actor => calls.Current!.Actor.ActorId!.Value;
    private Guid Operation => unit.CurrentOperationId!.Value;
    private OperationResult<T> Done<T>(T view, Guid resource, string action, string reason)
    { audit.Append(new(Operation, Actor, calls.Current!.Actor.Kind.ToString(), null, null, action, resource, "succeeded", reason, calls.Current.CorrelationId)); return OperationResult<T>.Completed(Operation, view, resource); }
    private OperationResult<T> Accepted<T>(T view, Guid resource, Guid work, string action, string reason)
    { Done(view, resource, action, reason); return OperationResult<T>.Accepted(Operation, work, view, resource); }
    public async Task<OperationResult<SelectionView>> CreateTargetSelection(CreateTargetSelectionCommand x, CancellationToken ct)
    { var s = await tasks.CreateSelectionAsync(Guid.NewGuid(), x.Input, Actor, ct);
      if (s.Mode != "Filter") return Done(s, s.Id, "tsk.selection.created", "explicit target selection");
      var w = await tasks.CreateWorkAsync(Guid.NewGuid(), s.SoftwareId, s.Id, Actor, "TargetSelection", ct);
      return Accepted(s, s.Id, w.WorkId, "tsk.selection.created", "restricted snapshot requested"); }
    public async Task<OperationResult<SelectionView>> PutTargetChunk(PutTargetChunkCommand x, CancellationToken ct)
    {
        return Done(await tasks.ChunkAsync(x.SelectionId, x.ChunkNo, x.InstanceIds, TaskProjection.ChunkDigest(x.InstanceIds), ct), x.SelectionId, "tsk.selection.chunk", "bounded explicit target chunk");
    }
    public async Task<OperationResult<SelectionView>> SealTargetSelection(SealTargetSelectionCommand x, CancellationToken ct) =>
        Done(await tasks.SealAsync(x.SelectionId, x.ExpectedRevision, x.ExpectedChunkCount, x.ExpectedMemberCount, ct), x.SelectionId, "tsk.selection.sealed", "member and chunk counts verified");
    public async Task<OperationResult<DeploymentView>> CreateDeployment(CreateDeploymentCommand x, CancellationToken ct)
    {
        var release = await Target(x.Input.SoftwareId, x.Input.TargetReleaseId, true, ct); var now = await time.NowAsync(ct);
        var d = await tasks.CreateAsync(Guid.NewGuid(), x.Input, release.Id, Actor, x.Input.Window ?? DefaultWindow(now), ct);
        var w = await tasks.CreateWorkAsync(Guid.NewGuid(), d.SoftwareId, d.Id, Actor, "Deployment", ct);
        return Accepted(d, d.Id, w.WorkId, "tsk.deployment.created", x.Input.Reason);
    }
    public async Task<OperationResult<DeploymentView>> ControlDeployment(ControlDeploymentCommand x, CancellationToken ct)
    {
        var d = await tasks.DeploymentAsync(x.DeploymentId, false, ct);
        if (x.Action == "Pause") return Done(await tasks.PauseAsync(d.Id, x.ExpectedRevision, x.Reason, ct), d.Id, "tsk.deployment.paused", x.Reason);
        if (x.Action == "Resume")
        { await Target(d.SoftwareId, d.TargetReleaseId, true, ct); return Done(await tasks.ResumeAsync(d.Id, x.ExpectedRevision, Actor, x.ReviewedFailures ?? [], ct), d.Id, "tsk.deployment.resumed", x.Reason); }
        throw new RequestRejectedException(RequestFailure.ValidationFailed);
    }
    public async Task<OperationResult<TaskWorkView>> CreateDeploymentControlWork(CreateDeploymentControlWorkCommand x, CancellationToken ct)
    {
        var w = await tasks.ControlAsync(x.DeploymentId, x.ExpectedRevision, Actor, x.Action, x.Window, ct);
        return Accepted(await tasks.WorkAsync(w.WorkId, ct), x.DeploymentId, w.WorkId, "tsk.deployment.control", x.Reason);
    }
    public async Task<OperationResult<TaskView>> ControlInstanceTask(ControlInstanceTaskCommand x, CancellationToken ct) => Done(
        await tasks.TaskControlAsync(x.TaskId, x.ExpectedRevision, x.Action, Actor, x.Reason, x.OnsiteEvidence, ct), x.TaskId, "tsk.task.control", x.Reason);
    public async Task<OperationResult<Guid>> ClaimInstanceTask(ClaimInstanceTaskCommand x, CancellationToken ct) => Done(await tasks.ClaimAsync(x.TaskId, ct), x.TaskId, "tsk.task.claimed", "stable execution attempt");
    public async Task<OperationResult<StartGrant>> StartInstanceTask(StartInstanceTaskCommand x, CancellationToken ct)
    {
        var original = await tasks.ExistingGrantAsync(x.AttemptId, ct); if (original is not null) return Done(original, x.AttemptId, "tsk.start.replayed", "original start permit");
        var t = await tasks.AttemptTaskAsync(x.AttemptId, false, ct); var d = await tasks.DeploymentAsync(t.DeploymentId, false, ct);
        var release = await Target(d.SoftwareId, d.TargetReleaseId, true, ct); var p = await packages.GetAsync(release.PackageId, false, true, ct);
        if (x.Input.Preflight.DownloadedSha256 != p.Sha256 || !x.Input.Preflight.DataProtectionConfirmed) throw new RequestRejectedException(RequestFailure.ChecksumMismatch);
        await tasks.CheckStartAsync(x.AttemptId, ct);
        var report = x.Input.StateReport; await VerifyReport(d.SoftwareId, report, ct);
        var direction = Direction(report, release.Version); if (direction is not null) throw new RequestRejectedException(direction == "ROLLBACK_REQUIRED" ? RequestFailure.RollbackRequired : direction == "CURRENT_VERSION_UNKNOWN" ? RequestFailure.CurrentVersionUnknown : RequestFailure.InvalidState);
        var accepted = await instances.ReportAsync(t.InstanceId, report, ct);
        // Only a newly accepted report or the exact current report may accompany a start permit.
        if (!accepted.Applied) throw new RequestRejectedException(RequestFailure.ReportConflict);
        return Done(await tasks.StartAsync(x.AttemptId, p.Id, p.Sha256!, ct), x.AttemptId, "tsk.start.authorized", "digest and data protection confirmed");
    }
    public async Task<OperationResult<ReceiptResult>> SubmitTaskReceipt(SubmitTaskReceiptCommand x, CancellationToken ct)
    {
        bool? applied = null;
        if (x.Input.StateReport is { } report && await tasks.CanApplyReportAsync(x.AttemptId, ct))
        { await VerifyReport(calls.Current!.Actor.SoftwareId!.Value, report, ct); applied = (await instances.ReportAsync(calls.Current.Actor.InstanceId!.Value, report, ct)).Applied; }
        var r = await tasks.ReceiveAsync(x.AttemptId, x.Input, TaskProjection.ReceiptDigest(x.Input), applied, ct);
        return Done(r, r.ReceiptId, "tsk.receipt.recorded", r.LateAfterClosure ? "late receipt retained without applying snapshot" : "sequenced receipt");
    }
    public async Task<OperationResult<IntegrationMaterialView>> RecordIntegrationMaterial(RecordIntegrationMaterialCommand x, CancellationToken ct) => Done(
        await materials.RecordAsync(Guid.NewGuid(), x.ReleaseId, x.Input, Actor, ct), x.ReleaseId, "rel.material.recorded", x.Input.Reason);
    public async Task<OperationResult<TaskLease?>> ClaimTaskWork(ClaimTaskWorkCommand x, CancellationToken ct) => OperationResult<TaskLease?>.Completed(Operation, await tasks.ClaimWorkAsync(x.WorkId, x.LeaseToken, ct), x.WorkId);
    public async Task<OperationResult<SelectionView>> MaterializeTaskTargets(MaterializeTaskTargetsCommand x, CancellationToken ct)
    {
        var w = await tasks.AuthorityAsync(x.Lease.Work.WorkId, true, ct);
        if (w.Kind != "TargetSelection" || w.LeaseToken != x.Lease.Token || w.LeaseGeneration != x.Lease.Generation || w.LeaseUntil <= await time.NowAsync(ct)) throw new RequestRejectedException(RequestFailure.InvalidState);
        if (!await authorization.HasPermissionAsync(w.InitiatorId, w.SoftwareId, "deployment.create", true, ct)) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        var filter = await tasks.FilterAsync(w.ResourceId, ct); var at = await time.NowAsync(ct); Guid? after = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(options.SnapshotTimeoutSeconds!.Value));
        while (true)
        { var ids = await facts.SnapshotPageAsync(filter, after, options.SelectionChunkSize!.Value, timeout.Token); if (ids.Count == 0) break;
          await tasks.AddSnapshotMembersAsync(w.ResourceId, ids, timeout.Token); after = ids[^1]; }
        await tasks.CompleteSnapshotAsync(w.ResourceId, at, ct); await tasks.AdvanceAsync(x.Lease, true, true, ct);
        return Done(await tasks.SelectionAsync(w.ResourceId, false, ct), w.ResourceId, "tsk.selection.snapshot", "single repeatable-read snapshot sealed");
    }
    public async Task<OperationResult<TaskWorkView>> AdvanceTaskWork(AdvanceTaskWorkCommand x, CancellationToken ct)
    {
        var w = await tasks.AuthorityAsync(x.Lease.Work.WorkId, false, ct); var permission = w.Kind is "Cancel" or "Reschedule" ? "deployment.control" : "deployment.create";
        var authorized = await authorization.HasPermissionAsync(w.InitiatorId, w.SoftwareId, permission, true, ct); var available = true;
        if (w.Kind != "TargetSelection")
        {
            var d = await tasks.DeploymentAsync(w.ResourceId, false, ct); var r = await releases.GetAsync(d.TargetReleaseId, true, ct);
            available = r.State == "Formal" && (await packages.GetAsync(r.PackageId, false, true, ct)).DownloadAvailable;
            if (authorized && available && w.Kind == "Deployment" && w.Stage == "Admission")
            {
                foreach (var id in await tasks.NextMembersAsync(x.Lease, ct))
                {
                    var f = await facts.GetAsync(id, false, ct); var reason = f?.SoftwareId != d.SoftwareId ? "OUT_OF_SCOPE" : f.Lifecycle != "Active" ? "INSTANCE_SUSPENDED" : Direction(f.Snapshot, r.Version);
                    Guid? retry = null;
                    if (d.Id != Guid.Empty && reason is null)
                    { var source = await tasks.RetryDeploymentAsync(d.Id, ct); if (source is { } old) { try { retry = await tasks.RetrySourceAsync(old, id, ct); } catch (RequestRejectedException e) when (e.Failure == RequestFailure.RetrySourceInvalid) { reason = "RETRY_SOURCE_INVALID"; } } }
                    await tasks.AdmitAsync(x.Lease, id, reason, retry, ct);
                }
            }
        }
        await tasks.AdvanceAsync(x.Lease, authorized, available, ct);
        return Done(await tasks.WorkAsync(w.WorkId, ct), w.WorkId, "tsk.work.advanced", "bounded persistent work step");
    }
    public async Task<OperationResult<bool>> FailTaskWork(FailTaskWorkCommand x, CancellationToken ct)
    { await tasks.FailWorkAsync(x.Lease, x.Code, ct); return Done(true, x.Lease.Work.WorkId, "tsk.work.failed", x.Code); }
    private async Task VerifyReport(Guid software, StateReport report, CancellationToken ct)
    { if (report.InstalledReleaseId is { } id) await releases.VerifyInstallationAsync(software, id, report.InstalledVersion, ct); }
    private async Task<ReleaseView> Target(Guid software, Guid? id, bool protect, CancellationToken ct)
    {
        if (id is { } rid) { var r = await releases.GetAsync(rid, protect, ct); if (r.SoftwareId != software) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
            if (r.State != "Formal" || !(await packages.GetAsync(r.PackageId, false, protect, ct)).DownloadAvailable) throw new RequestRejectedException(RequestFailure.InvalidState); return r; }
        foreach (var candidate in await releases.FormalCandidatesAsync(software, ct))
        { var r = await releases.GetAsync(candidate.Id,protect,ct); if(r.State == "Formal" && (await packages.GetAsync(r.PackageId,false,protect,ct)).DownloadAvailable) return r; }
        throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    }
    private TaskWindow DefaultWindow(DateTimeOffset utc)
    {
        options.Validate(); var zone = TimeZoneInfo.FindSystemTimeZoneById(site.Require().SiteTimeZone); var local = TimeZoneInfo.ConvertTime(utc, zone);
        var start = TimeOnly.ParseExact(options.DefaultStartLocalTime!, "HH:mm"); var end = TimeOnly.ParseExact(options.DefaultLatestStartLocalTime!, "HH:mm");
        var day = DateOnly.FromDateTime(local.DateTime); var from = day.ToDateTime(start, DateTimeKind.Unspecified);
        if (from <= local.DateTime) from = from.AddDays(1); var until = DateOnly.FromDateTime(from).ToDateTime(end, DateTimeKind.Unspecified); if (end < start) until = until.AddDays(1);
        if (zone.IsInvalidTime(from) || zone.IsInvalidTime(until) || zone.IsAmbiguousTime(from) || zone.IsAmbiguousTime(until)) throw new RequestRejectedException(RequestFailure.ValidationFailed);
        return new(TimeZoneInfo.ConvertTimeToUtc(from, zone), TimeZoneInfo.ConvertTimeToUtc(until, zone));
    }
    internal static string? Direction(StateReport? report, string target)
    {
        if (report?.InstallationState == "NotInstalled") return null;
        if (report?.InstallationState != "Installed" || !TryVersion(report.InstalledVersion, out var current) || !TryVersion(target, out var next)) return "CURRENT_VERSION_UNKNOWN";
        var cmp = next.CompareTo(current); return cmp < 0 ? "ROLLBACK_REQUIRED" : cmp == 0 ? "ALREADY_AT_TARGET" : null;
    }
    private static bool TryVersion(string? value, out (int Major, int Minor, int Patch) version)
    { version = default; var p = value?.Split('.'); if (p?.Length != 3 || p.Any(x => x.Length == 0 || x.Any(c => !char.IsAsciiDigit(c)) || x.Length > 1 && x[0] == '0')) return false;
      if (!int.TryParse(p[0], out var a) || !int.TryParse(p[1], out var b) || !int.TryParse(p[2], out var c)) return false; version = (a,b,c); return true; }
}
