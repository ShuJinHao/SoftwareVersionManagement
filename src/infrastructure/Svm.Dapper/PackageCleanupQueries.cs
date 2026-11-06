using Svm.Services.Contracts.Packages;

namespace Svm.Dapper;

internal sealed partial class PackageQueries
{
    public async Task<PackageCleanupStatus?> CleanupStatusAsync(Guid id, CancellationToken token)
    {
        var rows = await session.QueryAsync<CleanupRow>("""
            SELECT p."Id",p."Revision",p."State",c."Id" AS "WorkId",
              (p."Disabled" AND r."State"='Disabled') AS "Disabled",
              EXISTS(SELECT 1 FROM pkg.works w WHERE w."PackageId"=p."Id" AND w."Kind"<>'Cleanup' AND w."State" IN ('Pending','Running')) AS "FileWorkActive",
              (EXISTS(SELECT 1 FROM tsk.deployments d WHERE d."TargetReleaseId"=r."Id" AND d."State" NOT IN ('Rejected','Canceled','Completed'))
               OR EXISTS(SELECT 1 FROM tsk.tasks t WHERE t."TargetReleaseId"=r."Id" AND t."State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown'))) AS "TasksUnfinished"
            FROM pkg.packages p JOIN rel.releases r ON r."Id"=p."ReleaseId"
            LEFT JOIN pkg.cleanup_intents c ON c."PackageId"=p."Id"
            WHERE p."Id"=@id AND @kind='Human' AND iam.has_product(@subject,'package.clean',p."SoftwareId")
            """, Parameters(id, false), token);
        var row = rows.SingleOrDefault(); if (row is null) return null;
        var groups = await session.QueryAsync<DownloadGroupRow>("""
            SELECT d."NodeId",d."WorkerGeneration",count(*) AS "OpenCount",max(d."StartedAt") AS "LatestStartedAt"
            FROM pkg.download_sessions d JOIN pkg.packages p ON p."Id"=d."PackageId"
            WHERE p."Id"=@id AND @kind='Human' AND iam.has_product(@subject,'package.clean',p."SoftwareId")
              AND d."State"='Open' AND NOT EXISTS(SELECT 1 FROM pkg.download_stop_evidence s
                WHERE s."PackageId"=d."PackageId" AND s."NodeId"=d."NodeId" AND s."WorkerGeneration"=d."WorkerGeneration" AND s."StoppedAt">=d."StartedAt")
            GROUP BY d."NodeId",d."WorkerGeneration" ORDER BY d."NodeId",d."WorkerGeneration" LIMIT 51
            """, Parameters(id, false), token);
        var blockers = new List<string>();
        if (!row.Disabled) blockers.Add("RELEASE_NOT_DISABLED");
        if (row.FileWorkActive) blockers.Add("FILE_WORK_ACTIVE");
        if (row.TasksUnfinished) blockers.Add("TASKS_UNFINISHED");
        if (groups.Count > 0) blockers.Add("DOWNLOADS_OPEN");
        return new(row.Id, row.Revision, row.State, row.WorkId, blockers, groups.Take(50)
            .Select(g => new OpenDownloadGeneration(g.NodeId, g.WorkerGeneration, g.OpenCount, new DateTimeOffset(g.LatestStartedAt))).ToArray(), groups.Count > 50);
    }
    private sealed class DownloadGroupRow
    {
        public string NodeId { get; set; } = ""; public Guid WorkerGeneration { get; set; }
        public long OpenCount { get; set; } public DateTime LatestStartedAt { get; set; }
    }
    private sealed class CleanupRow
    {
        public Guid Id { get; set; } public long Revision { get; set; } public string State { get; set; } = "";
        public Guid? WorkId { get; set; } public bool Disabled { get; set; }
        public bool FileWorkActive { get; set; } public bool TasksUnfinished { get; set; }
    }
}
