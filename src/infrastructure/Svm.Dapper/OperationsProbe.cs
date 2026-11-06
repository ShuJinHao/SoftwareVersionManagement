using Svm.Services.Contracts.Framework;

namespace Svm.Dapper;

public sealed partial class ReadPersistenceProbe
{
    // Offline recovery only. Keyset pages; incomplete/oversized inventories fail closed.
    public async Task<RecoveryPackages> RecoveryPackagesAsync(CancellationToken token)
    {
        var packages = new List<RecoveryPackage>(); Guid? after = null;
        while (true)
        {
            var page = await _query.QueryAsync<RecoveryPackage>("SELECT \"Id\",\"SizeBytes\" AS Size,\"Sha256\" FROM pkg.packages WHERE \"State\"='Ready' AND (@after IS NULL OR \"Id\">@after) ORDER BY \"Id\" LIMIT 500",
                new { after }, token);
            packages.AddRange(page);
            if (packages.Count > 100000) throw new IOException("Recovery package inventory limit exceeded.");
            if (page.Count < 500) return new(DateTimeOffset.UtcNow, packages);
            after = page[^1].Id;
        }
    }
    // Fixed SQL only. Each aggregate is capped, server statement timeouts remain in force,
    // and neither payloads nor authentication material are selected.
    public async Task<OperationsSnapshot> OperationsAsync(CancellationToken token, int? replicaValiditySeconds = null)
    {
        var metrics = new List<OperationsMetric>();
        var sources = new (string Name, string Sql, string Time)[]
        {
            ("package.pending", "SELECT \"Id\",\"CreatedAt\" AS at FROM pkg.works WHERE \"State\" IN ('Pending','Running') ORDER BY \"CreatedAt\",\"Id\" LIMIT 1001", "CreatedAt"),
            ("package.failed", "SELECT \"Id\",\"CreatedAt\" AS at FROM pkg.works WHERE \"State\"='Failed' ORDER BY \"CreatedAt\",\"Id\" LIMIT 1001", "CreatedAt"),
            ("task.pending", "SELECT \"Id\",NULL::timestamptz AS at FROM tsk.works WHERE \"State\" IN ('Pending','Running') ORDER BY \"Id\" LIMIT 1001", "Unavailable: task work has no creation timestamp"),
            ("task.failed", "SELECT \"Id\",NULL::timestamptz AS at FROM tsk.works WHERE \"State\"='Failed' ORDER BY \"Id\" LIMIT 1001", "Unavailable: task work has no creation timestamp"),
            ("gitlab.pending", "SELECT \"Id\",\"CreatedAt\" AS at FROM rel.sync_works WHERE \"Stage\" NOT IN ('Completed','Historical','Conflict') ORDER BY \"CreatedAt\",\"Id\" LIMIT 1001", "CreatedAt"),
            ("outbox.pending", "SELECT \"MessageId\" AS \"Id\",\"SentTime\" AS at FROM framework.\"OutboxMessage\" ORDER BY \"SentTime\",\"SequenceNumber\" LIMIT 1001", "SentTime"),
            ("download.open", "SELECT \"Id\",\"StartedAt\" AS at FROM pkg.download_sessions WHERE \"State\"='Open' ORDER BY \"StartedAt\",\"Id\" LIMIT 1001", "StartedAt"),
            ("replica.unhealthy", "SELECT \"Id\",\"CheckedAt\" AS at FROM pkg.replicas WHERE \"State\" NOT IN ('Healthy','Deleted') ORDER BY \"CheckedAt\" NULLS FIRST,\"Id\" LIMIT 1001", "LastCheckAt; null means never checked"),
            ("recovery.unpausedDeployments", "SELECT \"Id\",\"CreatedAt\" AS at FROM tsk.deployments WHERE \"State\" NOT IN ('Paused','Completed','Canceled','Rejected') AND NOT (\"State\"='Preparing' AND \"PauseCodes\"<>'') ORDER BY \"CreatedAt\",\"Id\" LIMIT 1001", "CreatedAt"),
            ("recovery.validStartPermits", "SELECT \"Id\",\"MustBeginBefore\" AS at FROM tsk.attempts WHERE \"MustBeginBefore\">CURRENT_TIMESTAMP ORDER BY \"MustBeginBefore\",\"Id\" LIMIT 1001", "MustBeginBefore"),
            ("recovery.unfinishedTasks", "SELECT \"Id\",\"CreatedAt\" AS at FROM tsk.tasks WHERE \"State\" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown') ORDER BY \"CreatedAt\",\"Id\" LIMIT 1001", "CreatedAt")
        };
        foreach (var source in sources)
        {
            try
            {
                var rows = await _query.QueryAsync<ProbeAggregate>($"SELECT count(*) AS count,min(at) AS oldest FROM ({source.Sql}) s", null, token);
                var row = rows.Single();
                metrics.Add(new(source.Name, "Available", Math.Min(row.Count, 1000), row.Count > 1000, row.Oldest, source.Time, []));
            }
            catch (Exception e) when (e is PersistenceException or IOException or OperationCanceledException)
            { metrics.Add(new(source.Name, "Unavailable", null, false, null, source.Time, [])); }
        }
        foreach (var table in new[] { "pkg.works", "tsk.works", "rel.sync_works" })
        {
            var gitlab = table == "rel.sync_works";
            var state = gitlab ? "\"Stage\"" : "\"State\"";
            try
            {
                var rows = await _query.QueryAsync<ProbeWork>($"SELECT \"Id\",{state} AS State,\"Stage\",\"LeaseGeneration\" AS Generation,\"LeaseUntil\",\"LastErrorCode\" AS ErrorCode FROM {table} WHERE {state} NOT IN ('Completed','Historical') ORDER BY \"Id\" LIMIT 21", null, token);
                metrics.Add(new(table, "Available", null, rows.Count > 20, null, "LeaseUntil", rows.Take(20)
                    .Select(x => new OperationsWork(x.Id, x.State, x.Stage, x.Generation, x.LeaseUntil, x.ErrorCode)).ToArray()));
            }
            catch (Exception e) when (e is PersistenceException or IOException or OperationCanceledException)
            { metrics.Add(new(table, "Unavailable", null, false, null, "LeaseUntil", [])); }
        }
        if (replicaValiditySeconds is >= 10 and <= 7200)
        {
            try
            {
                var rows = await _query.QueryAsync<ProbeAggregate>("SELECT count(*) AS count,min(at) AS oldest FROM (SELECT \"CheckedAt\" AS at FROM pkg.replicas WHERE \"State\"='Healthy' AND (\"CheckedAt\" IS NULL OR \"CheckedAt\"<=@boundary) ORDER BY \"CheckedAt\" NULLS FIRST LIMIT 1001) s",
                    new { boundary = DateTimeOffset.UtcNow.AddSeconds(-replicaValiditySeconds.Value) }, token);
                var row = rows.Single(); metrics.Add(new("replica.stale", "Available", Math.Min(row.Count, 1000), row.Count > 1000, row.Oldest, "LastCheckAt", []));
            }
            catch (Exception e) when (e is PersistenceException or IOException or OperationCanceledException)
            { metrics.Add(new("replica.stale", "Unavailable", null, false, null, "LastCheckAt", [])); }
        }
        return new(DateTimeOffset.UtcNow, metrics.All(m => m.Status == "Available") ? "Available" : "Incomplete", metrics);
    }
    private sealed class ProbeAggregate { public long Count { get; set; } public DateTime? Oldest { get; set; } }
    private sealed class ProbeWork
    {
        public Guid Id { get; set; } public string State { get; set; } = ""; public string Stage { get; set; } = "";
        public long Generation { get; set; } public DateTime? LeaseUntil { get; set; } public string? ErrorCode { get; set; }
    }
}
