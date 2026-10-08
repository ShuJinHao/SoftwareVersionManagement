using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Dapper;

internal sealed class PackageQueries(ReadQuerySession session, ICallContext calls, PackageExecutionOptions execution) : IPackageQueries
{
    private CallActor Actor => calls.Current?.Actor ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
    private const string Visible = """
        ((@kind='Human' AND EXISTS(SELECT 1 FROM iam.users u JOIN iam.permissions perm ON perm."SubjectId"=u."Id"
           WHERE u."Id"=@subject AND u."IsEnabled" AND NOT u."MustChangePassword" AND perm."SoftwareId"=p."SoftwareId" AND perm."Operation"='software.read'))
         OR (@kind='Instance' AND p."SoftwareId"=@actorSoftware))
        """;
    public async Task<PackageView?> GetAsync(Guid id, bool upload, CancellationToken token)
    {
        var rows = await session.QueryAsync<PackageView>("""
            SELECT p."Id",p."ReleaseId",p."State",p."SizeBytes",p."Sha256",p."ExpectedSize",p."ExpectedSha256",cp."HealthyReplicaCount",
              (r."State" IN ('Test','Formal') AND p."State"='Ready' AND NOT p."Disabled" AND cp."HealthyReplicaCount">0) AS "DownloadAvailable",
              CASE WHEN r."State" IN ('Test','Formal') AND p."State"='Ready' AND NOT p."Disabled" AND cp."HealthyReplicaCount">0
                THEN '/api/v1/packages/'||p."Id"::text||'/content' ELSE NULL END AS "DownloadPath",
              COALESCE(w."Stage",'AwaitingUpload') AS "ProcessingStage",w."LastErrorCode",p."Revision",p."UploadId",p."FileName"
            FROM pkg.packages p JOIN rel.releases r ON r."Id"=p."ReleaseId"
            CROSS JOIN LATERAL (SELECT count(*)::int AS "HealthyReplicaCount" FROM pkg.replicas c WHERE c."PackageId"=p."Id" AND c."State"='Healthy'
                AND c."CheckedAt">clock_timestamp()-make_interval(secs=>@freshSeconds)) cp
            LEFT JOIN LATERAL (SELECT "Stage","LastErrorCode" FROM pkg.works WHERE "PackageId"=p."Id" ORDER BY "CreatedAt" DESC,"Id" DESC LIMIT 1) w ON true
            WHERE ((@upload AND p."UploadId"=@id) OR (NOT @upload AND p."Id"=@id)) AND
            """ + Visible, Parameters(id, upload), token);
        return rows.SingleOrDefault();
    }
    public async Task<PackageWorkView?> WorkAsync(Guid id, CancellationToken token)
    {
        var rows = await session.QueryAsync<WorkRow>("""
            SELECT w."Id",w."Kind",w."State",w."Stage",w."LastErrorCode",w."CreatedAt",w."CompletedAt",w."Revision",
              (SELECT count(*)::int FROM pkg.replicas c WHERE c."PackageId"=p."Id" AND c."State"='Healthy') AS "CompletedItems"
            FROM pkg.works w JOIN pkg.packages p ON p."Id"=w."PackageId" WHERE w."Id"=@id AND
            """ + Visible, Parameters(id, false), token);
        var r = rows.SingleOrDefault(); return r is null ? null : new(r.Id, r.Kind, r.State, r.Stage, r.CompletedItems, 2, r.LastErrorCode, [], r.CreatedAt, r.CompletedAt, r.Revision);
    }
    private object Parameters(Guid id, bool upload) => new { id, upload, kind = Actor.Kind.ToString(), subject = Actor.ActorId,
        actorSoftware = Actor.SoftwareId, freshSeconds = execution.ReplicaCheckSeconds * 2 };
    private sealed class WorkRow
    {
        public Guid Id { get; set; } public string Kind { get; set; } = ""; public string State { get; set; } = ""; public string Stage { get; set; } = "";
        public int CompletedItems { get; set; } public string? LastErrorCode { get; set; } public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; } public long Revision { get; set; }
    }
}
