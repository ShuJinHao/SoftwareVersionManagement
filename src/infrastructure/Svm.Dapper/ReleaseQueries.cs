using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Dapper;

public static class ReleaseQueryRegistration
{
    public static IServiceCollection AddSvmReleaseQueries(this IServiceCollection services) => services.AddScoped<IReleaseQueries, ReleaseQueries>().AddScoped<IPackageQueries, PackageQueries>();
}
internal sealed class ReleaseQueries(ReadQuerySession session, ICallContext calls, PackageExecutionOptions options) : IReleaseQueries
{
    private CallActor Actor => calls.Current?.Actor ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
    private const string Projection = """
        SELECT r."Id",r."SoftwareId",r."Major",r."Minor",r."Patch",r."State",r."ChangeLevel",r."ChangeSummary",r."ChangeReason",r."PackageId",
          r."CreatedBy",r."CreatedAt",r."DisabledAt",r."DisableReason",r."Revision",p."SizeBytes",p."Sha256",
          (r."State" IN ('Test','Formal') AND p."State"='Ready' AND NOT p."Disabled" AND EXISTS
             (SELECT 1 FROM pkg.replicas cp WHERE cp."PackageId"=p."Id" AND cp."State"='Healthy'
                AND cp."CheckedAt">clock_timestamp()-make_interval(secs=>@freshSeconds))) AS "DownloadAvailable"
        FROM rel.releases r JOIN pkg.packages p ON p."Id"=r."PackageId"
        """;
    private const string Visible = """
        ((@kind='Human' AND EXISTS(SELECT 1 FROM iam.users u JOIN iam.permissions perm ON perm."SubjectId"=u."Id"
           WHERE u."Id"=@subject AND u."IsEnabled" AND NOT u."MustChangePassword" AND perm."SoftwareId"=r."SoftwareId" AND perm."Operation"=@permission))
         OR (@kind='Instance' AND r."SoftwareId"=@actorSoftware))
        """;
    public async Task<ReleaseView?> GetAsync(Guid id, CancellationToken token) =>
        (await session.QueryAsync<Row>(Projection + " WHERE r.\"Id\"=@id AND " + Visible, Parameters(id: id), token)).SingleOrDefault()?.View;
    public async Task<ReleasePage<ReleaseView>> ListAsync(ReleaseListInput input, CancellationToken token)
    {
        var rows = await Rows(input, false, token); var items = rows.Take(input.PageSize).ToArray();
        return new(items.Select(x => x.View).ToArray(), rows.Count > input.PageSize ? items[^1].Position : null);
    }
    public async Task<ReleasePage<ClientReleaseView>> ClientListAsync(ReleaseListInput input, CancellationToken token)
    {
        var rows = await Rows(input with { State = input.Channel == "Test" ? "Test" : "Formal" }, true, token);
        var items = rows.Take(input.PageSize).ToArray();
        return new(items.Select(r => new ClientReleaseView(r.Id, r.View.Version, r.State, r.ChangeSummary, r.ChangeReason,
            r.PackageId, r.SizeBytes!.Value, r.Sha256!, $"/api/v1/packages/{r.PackageId:D}/content")).ToArray(), rows.Count > input.PageSize ? items[^1].Position : null);
    }
    private Task<IReadOnlyList<Row>> Rows(ReleaseListInput input, bool available, CancellationToken token) => session.QueryAsync<Row>(
        "SELECT * FROM (" + Projection + " WHERE r.\"SoftwareId\"=@software AND " + Visible + """
          AND (CAST(@state AS text) IS NULL OR r."State"=@state)
          AND (CAST(@channel AS text) IS NULL OR (@channel='Formal' AND r."State"='Formal') OR (@channel='Test' AND r."State" IN ('Staging','Test','Disabled')))
          AND (CAST(@afterId AS uuid) IS NULL OR (r."Major",r."Minor",r."Patch",r."Id")<(@major,@minor,@patch,@afterId))
        ) s WHERE (NOT @available OR s."DownloadAvailable") ORDER BY s."Major" DESC,s."Minor" DESC,s."Patch" DESC,s."Id" DESC LIMIT @take
        """, new { software = input.SoftwareId, state = input.State, channel = input.Channel, afterId = input.After?.Id,
            major = input.After?.Major ?? 0, minor = input.After?.Minor ?? 0, patch = input.After?.Patch ?? 0,
            take = input.PageSize + 1, available, subject = Actor.ActorId, kind = Actor.Kind.ToString(), actorSoftware = Actor.SoftwareId,
            permission = "software.read", freshSeconds = options.ReplicaCheckSeconds * 2 }, token);
    public async Task<EvidencePage> EvidenceAsync(Guid releaseId, int size, Guid? after, CancellationToken token)
    {
        if (await GetAsync(releaseId, token) is null) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var rows = await session.QueryAsync<EvidenceRow>("""
            SELECT "Id","InstanceId","InstalledReleaseId" AS "ReleaseId","InstalledVersion","InstalledAt","ReceivedAt","ReportedRunningState"
            FROM ins.installation_evidence WHERE "InstalledReleaseId"=@releaseId AND "InstallationState"='Installed'
              AND (CAST(@after AS uuid) IS NULL OR "Id">@after) ORDER BY "Id" LIMIT @take
            """, new { releaseId, after, take = size + 1 }, token);
        var items = rows.Take(size).ToArray(); return new(items.Select(x => x.View).ToArray(), rows.Count > size ? items[^1].Id : null);
    }
    public async Task<DownloadAuditPage> DownloadsAsync(Guid softwareId, int size, Guid? after, CancellationToken token)
    {
        var rows = await session.QueryAsync<DownloadRow>("""
            SELECT d."Id" AS "RequestId",d."PackageId",d."SubjectId",d."ActorKind",d."EmployeeNo",d."StartedAt",d."EndedAt",d."BytesSent",d."State"
            FROM pkg.download_sessions d JOIN pkg.packages p ON p."Id"=d."PackageId"
            WHERE p."SoftwareId"=@softwareId AND EXISTS(SELECT 1 FROM iam.users u JOIN iam.permissions perm ON perm."SubjectId"=u."Id"
              WHERE u."Id"=@subject AND u."IsEnabled" AND NOT u."MustChangePassword" AND perm."SoftwareId"=p."SoftwareId" AND perm."Operation"='audit.read')
              AND (CAST(@after AS uuid) IS NULL OR d."Id">@after) ORDER BY d."Id" LIMIT @take
            """, new { softwareId, subject = Actor.ActorId, after, take = size + 1 }, token);
        var items = rows.Take(size).ToArray(); return new(items.Select(x => x.View).ToArray(), rows.Count > size ? items[^1].RequestId : null);
    }
    private object Parameters(Guid? id = null) => new { id, subject = Actor.ActorId, kind = Actor.Kind.ToString(), actorSoftware = Actor.SoftwareId,
        permission = "software.read", freshSeconds = options.ReplicaCheckSeconds * 2 };
    private sealed class EvidenceRow
    {
        public Guid Id { get; set; } public Guid InstanceId { get; set; } public Guid ReleaseId { get; set; } public string InstalledVersion { get; set; } = "";
        public DateTimeOffset? InstalledAt { get; set; } public DateTimeOffset ReceivedAt { get; set; } public string ReportedRunningState { get; set; } = "";
        public TestEvidenceView View => new(Id, InstanceId, ReleaseId, InstalledVersion, InstalledAt, ReceivedAt, ReportedRunningState);
    }
    private sealed class DownloadRow
    {
        public Guid RequestId { get; set; } public Guid PackageId { get; set; } public Guid? SubjectId { get; set; }
        public string ActorKind { get; set; } = ""; public string? EmployeeNo { get; set; } public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; } public long? BytesSent { get; set; } public string State { get; set; } = "";
        public DownloadAuditView View => new(RequestId, PackageId, SubjectId, ActorKind, EmployeeNo, StartedAt, EndedAt, BytesSent, State);
    }
    private sealed class Row
    {
        public Guid Id { get; set; } public Guid SoftwareId { get; set; } public int Major { get; set; } public int Minor { get; set; } public int Patch { get; set; }
        public string State { get; set; } = ""; public string ChangeLevel { get; set; } = ""; public string ChangeSummary { get; set; } = ""; public string ChangeReason { get; set; } = "";
        public Guid PackageId { get; set; } public bool DownloadAvailable { get; set; } public Guid CreatedBy { get; set; } public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? DisabledAt { get; set; } public string? DisableReason { get; set; } public long Revision { get; set; }
        public long? SizeBytes { get; set; } public string? Sha256 { get; set; }
        public ReleaseView View => new(Id, SoftwareId, $"{Major}.{Minor}.{Patch}", State, ChangeLevel, ChangeSummary, ChangeReason,
            PackageId, DownloadAvailable, CreatedBy, CreatedAt, DisabledAt, DisableReason, Revision);
        public ReleasePosition Position => new(Major, Minor, Patch, Id);
    }
}
