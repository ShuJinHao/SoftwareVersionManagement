using Microsoft.EntityFrameworkCore;
using Npgsql;
using Svm.Core.Tasks;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Tasks;

internal sealed class TaskRepository(SvmDbContext db, IUnitOfWork unit) : ITaskRepository
{
    private void Write() { if (unit.CurrentOperationId is null || db.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
    private async Task<T?> One<T>(string sql, Guid id, bool protect, Func<T, Guid> key, CancellationToken ct) where T : class
    {
        if (protect) Write(); var local = db.Set<T>().Local.SingleOrDefault(x => key(x) == id); if (local is not null) return local;
        var q = db.Set<T>().FromSqlRaw(sql + (protect ? " FOR UPDATE" : ""), id);
        try { return await (protect ? q.AsTracking() : q.AsNoTracking()).SingleOrDefaultAsync(ct); }
        // Npgsql's non-retrying EF strategy wraps serialization/deadlock failures from reads.
        // These SQLSTATE values prove abort; preserve them for the existing fresh-scope retry policy.
        catch (InvalidOperationException e) when (e.InnerException is PostgresException { SqlState: "40001" or "40P01" } postgres)
        { throw new PersistenceException(PersistenceFailure.DependencyUnavailable, unit.CurrentOperationId, postgres.SqlState); }
    }
    public Task<TargetSelection?> SelectionAsync(Guid id, bool protect, CancellationToken ct) => One<TargetSelection>("SELECT * FROM tsk.target_selections WHERE \"Id\"={0}", id, protect, x => x.Id.Value, ct);
    public Task<Deployment?> DeploymentAsync(Guid id, bool protect, CancellationToken ct) => One<Deployment>("SELECT * FROM tsk.deployments WHERE \"Id\"={0}", id, protect, x => x.Id.Value, ct);
    public Task<InstanceTask?> TaskAsync(Guid id, bool protect, CancellationToken ct) => One<InstanceTask>("SELECT * FROM tsk.tasks WHERE \"Id\"={0}", id, protect, x => x.Id.Value, ct);
    public Task<ExecutionAttempt?> AttemptAsync(Guid id, bool protect, CancellationToken ct) => One<ExecutionAttempt>("SELECT * FROM tsk.attempts WHERE \"Id\"={0}", id, protect, x => x.Id, ct);
    public async Task<IReadOnlyList<TaskWork>> RecoverableAsync(Guid resource,CancellationToken ct) => await db.Set<TaskWork>().FromSqlInterpolated($"SELECT * FROM tsk.works WHERE \"ResourceId\"={resource} AND (NOT \"Accepted\" OR \"State\"='Failed') ORDER BY \"Id\" FOR UPDATE").AsTracking().ToListAsync(ct);
    public Task<TaskWork?> WorkAsync(Guid id, bool protect, CancellationToken ct) => One<TaskWork>("SELECT * FROM tsk.works WHERE \"Id\"={0}", id, protect, x => x.Id.Value, ct);
    public Task<SelectionChunk?> ChunkAsync(Guid selection, int number, CancellationToken ct) => db.Set<SelectionChunk>().AsNoTracking().SingleOrDefaultAsync(x => x.SelectionId == new StrongId<TargetSelection>(selection) && x.Number == number, ct);
    public async Task<int> AppendMembersAsync(Guid selection, IReadOnlyList<Guid> ids, CancellationToken ct)
    { Write(); return await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO tsk.target_members (\"SelectionId\",\"InstanceId\") SELECT {selection}, unnest({ids.ToArray()}) ON CONFLICT DO NOTHING", ct); }
    public async Task<IReadOnlyList<Guid>> MembersAsync(Guid selection, Guid? after, int take, CancellationToken ct) => await db.Set<TargetMember>().FromSqlRaw(
        "SELECT * FROM tsk.target_members WHERE \"SelectionId\"={0} AND ({1}::uuid IS NULL OR \"InstanceId\">{1}) ORDER BY \"InstanceId\" LIMIT {2}", selection, (object?)after ?? DBNull.Value, take).AsNoTracking().Select(x => x.InstanceId).ToListAsync(ct);
    public Task<TaskBatch?> BatchAsync(Guid id, bool protect, CancellationToken ct) => One<TaskBatch>("SELECT * FROM tsk.batches WHERE \"Id\"={0}",id,protect,x=>x.Id.Value,ct);
    public Task<TaskBatch?> LastBatchAsync(Guid deployment, CancellationToken ct) => db.Set<TaskBatch>().FromSqlRaw("SELECT * FROM tsk.batches WHERE \"DeploymentId\"={0} ORDER BY \"Ordinal\" DESC LIMIT 1 FOR UPDATE",deployment).AsTracking().SingleOrDefaultAsync(ct);
    public async Task<IReadOnlyList<TaskBatch>> BatchesAsync(Guid deployment, bool protect, CancellationToken ct)
    { if (protect) Write(); var q = db.Set<TaskBatch>().FromSqlRaw("SELECT * FROM tsk.batches WHERE \"DeploymentId\"={0} AND (\"State\"='Open' OR \"FailureRevision\">\"ReviewedFailureRevision\" OR \"Id\"=(SELECT \"Id\" FROM tsk.batches WHERE \"DeploymentId\"={0} AND \"State\"='Pending' ORDER BY \"Ordinal\" LIMIT 1)) ORDER BY \"Id\"" + (protect ? " FOR UPDATE" : ""), deployment);
      var rows = await (protect ? q.AsTracking() : q.AsNoTracking()).ToListAsync(ct); return db.Set<TaskBatch>().Local.Where(x => x.DeploymentId == new StrongId<Deployment>(deployment)).Concat(rows).DistinctBy(x => x.Id).ToArray(); }
    public Task<InstanceTask?> RetryAsync(Guid deployment, Guid instance, CancellationToken ct) => db.Set<InstanceTask>().AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == new StrongId<Deployment>(deployment) && x.InstanceId == instance, ct);
    public async Task<IReadOnlyList<InstanceTask>> TasksAsync(Guid deployment, Guid? after, int take, bool unbatched, bool protect, CancellationToken ct)
    {
        if (protect) Write(); var q = db.Set<InstanceTask>().FromSqlRaw("SELECT * FROM tsk.tasks WHERE \"DeploymentId\"={0} AND ({1}::uuid IS NULL OR \"Id\">{1}) AND (NOT {2} OR \"BatchId\" IS NULL) ORDER BY \"Id\" LIMIT {3}" + (protect ? " FOR UPDATE" : ""), deployment, (object?)after ?? DBNull.Value, unbatched, take);
        return await (protect ? q.AsTracking() : q.AsNoTracking()).ToListAsync(ct);
    }
    public Task<TaskReceipt?> ReceiptAsync(Guid attempt, Guid eventId, long sequence, CancellationToken ct) => db.Set<TaskReceipt>().AsNoTracking().FirstOrDefaultAsync(x => x.AttemptId == attempt && (x.EventId == eventId || x.Sequence == sequence), ct);
    public async Task<InstanceExecutionGuard?> GuardAsync(Guid instance, CancellationToken ct)
    { Write(); await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({instance.ToString()}, 71007))", ct);
      var local = db.Set<InstanceExecutionGuard>().Local.SingleOrDefault(x => x.InstanceId == instance); if (local is not null) return local;
      return await db.Set<InstanceExecutionGuard>().FromSqlInterpolated($"SELECT * FROM tsk.instance_execution_guards WHERE \"InstanceId\"={instance} FOR UPDATE").AsTracking().SingleOrDefaultAsync(ct); }
    public async Task ReleaseGuardAsync(Guid instance, Guid task, CancellationToken ct)
    { Write(); var local = db.Set<InstanceExecutionGuard>().Local.SingleOrDefault(x => x.InstanceId == instance && x.TaskId == new StrongId<InstanceTask>(task));
      if (local is not null) db.Entry(local).State = EntityState.Detached;
      await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM tsk.instance_execution_guards WHERE \"InstanceId\"={instance} AND \"TaskId\"={task}", ct); }
    public async Task<IReadOnlyList<Guid>> PendingAsync(DateTimeOffset now, int take, CancellationToken ct) => (await db.Set<TaskWork>().AsNoTracking()
        .Where(x => x.Accepted && (x.State == "Pending" || x.State == "Running") && (x.LeaseUntil == null || x.LeaseUntil <= now) && x.NextScanAt <= now).OrderBy(x => x.NextScanAt).ThenBy(x => x.Id).Take(take).Select(x => x.Id).ToListAsync(ct)).Select(x => x.Value).ToArray();
    public Task<bool> HasDispatchAsync(Guid work, long seq, Guid eventId, CancellationToken ct) => db.Set<TaskDispatch>().AsNoTracking().AnyAsync(x => x.Id == eventId && x.WorkId == new StrongId<TaskWork>(work) && x.Sequence == seq, ct);
    public async Task<DeploymentCounts> CountsAsync(Guid deployment, CancellationToken ct)
    {
        var local = db.Set<InstanceTask>().Local.Where(x => x.DeploymentId == new StrongId<Deployment>(deployment)).ToArray(); var ids = local.Select(x => x.Id).ToArray();
        var counts = await db.Set<InstanceTask>().AsNoTracking().Where(x => x.DeploymentId == new StrongId<Deployment>(deployment) && !ids.Contains(x.Id)).GroupBy(x => x.State).Select(x => new { State = x.Key, Count = x.Count() }).ToListAsync(ct);
        var dict = counts.ToDictionary(x => x.State, x => x.Count); foreach (var t in local) dict[t.State] = dict.GetValueOrDefault(t.State) + 1;
        return new(dict.GetValueOrDefault("Succeeded"), dict.GetValueOrDefault("Failed"), dict.GetValueOrDefault("Canceled"), dict.GetValueOrDefault("ClosedUnknown"), dict.Where(x => x.Key is not ("Succeeded" or "Failed" or "Canceled" or "ClosedUnknown")).Sum(x => x.Value));
    }
    public async Task<BatchCounts> BatchCountsAsync(Guid batch, DateTimeOffset now, CancellationToken ct)
    {
        var local = db.Set<InstanceTask>().Local.Where(x => x.BatchId == new StrongId<TaskBatch>(batch)).ToArray(); var ids = local.Select(x => x.Id.Value).ToArray();
        var rows = await db.Database.SqlQueryRaw<BatchCountRow>("""
            SELECT count(*) FILTER (WHERE t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown'))::int AS "Unfinished",
              count(*) FILTER (WHERE t."IsDeferred" AND t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown'))::int AS "Deferred",
              count(*) FILTER (WHERE t."State"='Failed')::int AS "Failed",
              count(*) FILTER (WHERE t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown') AND NOT t."IsDeferred" AND b."OpenedAt" IS NOT NULL
                AND COALESCE(t."ResponseDeadlineAt",greatest(b."OpenedAt",t."NotBefore") + d."ResultWaitSeconds" * interval '1 second') < {1})::int AS "TimedOut",
              min(COALESCE(t."ResponseDeadlineAt",greatest(b."OpenedAt",t."NotBefore") + d."ResultWaitSeconds" * interval '1 second')) FILTER
                (WHERE t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown') AND NOT t."IsDeferred" AND b."OpenedAt" IS NOT NULL) AS "EarliestDeadline"
            FROM tsk.tasks t JOIN tsk.batches b ON b."Id"=t."BatchId" JOIN tsk.deployments d ON d."Id"=t."DeploymentId"
            WHERE t."BatchId"={0} AND NOT (t."Id"=ANY({2}))
            """, batch, now, ids).SingleAsync(ct);
        var b = db.Set<TaskBatch>().Local.SingleOrDefault(x => x.Id.Value == batch) ?? await db.Set<TaskBatch>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == new StrongId<TaskBatch>(batch), ct);
        if (b is not null)
        {
            var d = await DeploymentAsync(b.DeploymentId.Value, false, ct);
            foreach (var t in local)
            {
                if (t.State == "Failed") rows.Failed++;
                if (t.Ended) continue; rows.Unfinished++; if (t.IsDeferred) { rows.Deferred++; continue; }
                var deadline = t.ResponseDeadlineAt ?? (b.OpenedAt is { } at ? (at > t.NotBefore ? at : t.NotBefore).AddSeconds(d!.ResultWaitSeconds) : (DateTimeOffset?)null);
                if (deadline < now) rows.TimedOut++; if (deadline is not null && (rows.EarliestDeadline is null || deadline < rows.EarliestDeadline)) rows.EarliestDeadline = deadline;
            }
        }
        return new(rows.Unfinished, rows.Deferred, rows.Failed, rows.TimedOut, rows.EarliestDeadline);
    }
    public void Add(TargetSelection x) { Write(); db.Add(x); }
    public void Add(SelectionChunk x) { Write(); db.Add(x); }
    public void Add(Deployment x) { Write(); db.Add(x); }
    public void Add(AdmissionItem x) { Write(); db.Add(x); }
    public void Add(TaskBatch x) { Write(); db.Add(x); }
    public void Add(InstanceTask x) { Write(); db.Add(x); }
    public void Add(ExecutionAttempt x) { Write(); db.Add(x); }
    public void Add(TaskReceipt x) { Write(); db.Add(x); }
    public void Add(InstanceExecutionGuard x) { Write(); db.Add(x); }
    public void Add(TaskWork x) { Write(); db.Add(x); }
    public void Add(TaskDispatch x) { Write(); db.Add(x); }
    public void Add(TaskControlItem x) { Write(); db.Add(x); }
}
internal sealed class BatchCountRow
{ public int Unfinished { get; set; } public int Deferred { get; set; } public int Failed { get; set; } public int TimedOut { get; set; } public DateTimeOffset? EarliestDeadline { get; set; } }
internal sealed class TaskTime(SvmDbContext db) : ITaskTime
{ public Task<DateTimeOffset> NowAsync(CancellationToken ct) => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct); }
