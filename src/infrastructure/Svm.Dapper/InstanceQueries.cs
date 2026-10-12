using System.Text.Json;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Framework;

namespace Svm.Dapper;

internal sealed class InstanceQueries(ReadQuerySession session,CatalogReadScope scope,TimeProvider? clock=null) : IInstanceQueries
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    internal const string Projection = """
        SELECT i."Id",i."SoftwareId",i."DeviceId",d."DeviceNo",d."Name" AS "DeviceName",pr."Id" AS "ProcessId",
          pr."Code" AS "ProcessCode",pr."Name" AS "ProcessName",i."Lifecycle",i."Revision",ss."SnapshotJson"::text AS "SnapshotJson",ss."LastAcceptedAt"
        FROM ins.instances i JOIN ins.devices d ON d."Id"=i."DeviceId" JOIN ins.processes pr ON pr."Id"=d."ProcessId"
        JOIN ins.instance_snapshots ss ON ss."InstanceId"=i."Id"
        """;
    private const string Visible = """
        pr."SiteId"=@siteId AND EXISTS(SELECT 1 FROM iam.permissions p JOIN iam.users u ON u."Id"=p."SubjectId"
          WHERE p."SubjectId"=@subjectId AND p."SoftwareId"=i."SoftwareId" AND p."Operation"='instance.read' AND u."IsEnabled" AND NOT u."MustChangePassword")
        """;
    public async Task<InstanceView?> GetAsync(Guid id,CancellationToken token,string permission="instance.read")
    {
        if(permission is not ("instance.read" or "instance.manage")) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        await scope.EnsureAsync(null,token);
        var row=(await session.QueryAsync<InstanceRow>(Projection+" WHERE i.\"Id\"=@id AND "+Visible.Replace("'instance.read'","@permission"),new { id,subjectId=scope.SubjectId,siteId=scope.Site.SiteId,permission },token)).SingleOrDefault();
        return row is null ? null : row.View(scope.Site,Now);
    }
    public async Task<InstancePage<InstanceView>> ListAsync(InstanceListInput x,CancellationToken token)
    {
        await scope.EnsureAsync(null,token); var now=Now;
        var rows=await session.QueryAsync<InstanceRow>(Projection+" WHERE "+Visible+"""
          AND i."SoftwareId"=@softwareId AND (CAST(@after AS uuid) IS NULL OR i."Id">@after)
          AND (CAST(@processId AS uuid) IS NULL OR pr."Id"=@processId) AND (CAST(@deviceId AS uuid) IS NULL OR i."DeviceId"=@deviceId)
          AND (CAST(@deviceNo AS text) IS NULL OR d."DeviceNo" LIKE @deviceNo ESCAPE '\')
          AND (CAST(@lifecycle AS text) IS NULL OR i."Lifecycle"=@lifecycle)
          AND (CAST(@running AS text) IS NULL OR ss."SnapshotJson"->>'RunningState'=@running)
          AND (CAST(@release AS uuid) IS NULL OR ss."SnapshotJson"->>'InstalledReleaseId'=CAST(@release AS text))
          AND (CAST(@ip AS text) IS NULL OR ss."SnapshotJson"->'ReportedIps' ? CAST(@ip AS text))
          AND (CAST(@freshness AS text) IS NULL OR CASE WHEN ss."LastAcceptedAt" IS NULL THEN 'NeverReported'
               WHEN ss."LastAcceptedAt"<@boundary THEN 'Unknown' ELSE 'Fresh' END=@freshness)
          ORDER BY i."Id" LIMIT @take
          """,new { subjectId=scope.SubjectId,siteId=scope.Site.SiteId,softwareId=x.Filter.SoftwareId,after=x.After,processId=x.Filter.ProcessId,
            deviceId=x.Filter.DeviceId,deviceNo=CatalogReadScope.Prefix(x.Filter.DeviceNo),lifecycle=x.Filter.Lifecycle,running=x.Filter.RunningState,
            release=x.Filter.InstalledReleaseId,ip=x.Filter.ReportedIp,freshness=x.Filter.Freshness,boundary=now.AddMinutes(-5),take=x.PageSize+1 },token);
        var items=rows.Take(x.PageSize).Select(r=>r.View(scope.Site,now)).ToArray(); return new(items,rows.Count>x.PageSize?items[^1].Id:null);
    }
    public async Task<InstancePage<InstallationHistoryView>> HistoryAsync(Guid id,int size,Guid? after,CancellationToken token)
    {
        if(await GetAsync(id,token) is null) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var rows=await session.QueryAsync<HistoryRow>("""
            SELECT e."Id",e."InstanceId",e."InstallationState",e."InstalledReleaseId",e."InstalledVersion",e."InstalledAt",e."ReceivedAt",e."ReportedRunningState"
            FROM ins.installation_evidence e JOIN ins.instances i ON i."Id"=e."InstanceId"
            WHERE e."InstanceId"=@id AND EXISTS(SELECT 1 FROM iam.permissions p JOIN iam.users u ON u."Id"=p."SubjectId"
              WHERE p."SubjectId"=@subjectId AND p."SoftwareId"=i."SoftwareId" AND p."Operation"='instance.read' AND u."IsEnabled" AND NOT u."MustChangePassword")
              AND (CAST(@after AS uuid) IS NULL OR (e."ReceivedAt",e."Id")>(
                SELECT a."ReceivedAt",a."Id" FROM ins.installation_evidence a WHERE a."InstanceId"=@id AND a."Id"=@after))
            ORDER BY e."ReceivedAt",e."Id" LIMIT @take
            """,new { id,subjectId=scope.SubjectId,after,take=size+1 },token);
        var items=rows.Take(size).Select(x=>x.View()).ToArray(); return new(items,rows.Count>size?items[^1].Id:null);
    }
    public async Task<InstancePage<GrantView>> GrantsAsync(Guid softwareId,int size,Guid? after,CancellationToken token)
    {
        await scope.EnsureAsync(null,token); var now=Now;
        var rows=await session.QueryAsync<GrantRow>("""
          SELECT g."Id",g."SoftwareId",g."DeviceIds",g."ExpiresAt",g."MaxInstances",g."UsedCount",g."RevokedAt",g."Revision"
          FROM iam.enrollment_grants g WHERE g."SoftwareId"=@softwareId AND (CAST(@after AS uuid) IS NULL OR g."Id">@after)
          AND EXISTS(SELECT 1 FROM iam.permissions p JOIN iam.users u ON u."Id"=p."SubjectId" WHERE p."SubjectId"=@subjectId AND
            p."SoftwareId"=g."SoftwareId" AND p."Operation"='enrollment.manage' AND u."IsEnabled" AND NOT u."MustChangePassword")
          ORDER BY g."Id" LIMIT @take
          """,new { softwareId,subjectId=scope.SubjectId,after,take=size+1 },token);
        var items=rows.Take(size).Select(x=>new GrantView(x.Id,x.SoftwareId,x.DeviceIds,null,x.RevokedAt is not null?"Revoked":x.ExpiresAt<=now?"Expired":x.UsedCount>=x.MaxInstances?"Exhausted":"Active",x.ExpiresAt,x.MaxInstances,x.UsedCount,x.Revision)).ToArray();
        return new(items,rows.Count>size?items[^1].Id:null);
    }
    public async Task<InstancePage<CredentialView>> CredentialsAsync(Guid instanceId,int size,Guid? after,CancellationToken token)
    {
        await scope.EnsureAsync(null,token);
        var rows=await session.QueryAsync<CredentialRow>("""
            SELECT c."Id",c."SubjectId",c."ExpiresAt",c."RevokedAt",c."Revision" FROM iam.instance_credentials c JOIN iam.instance_subjects s ON s."Id"=c."SubjectId"
            WHERE c."SubjectId"=@instanceId AND (CAST(@after AS uuid) IS NULL OR c."Id">@after) AND EXISTS(SELECT 1 FROM iam.permissions p JOIN iam.users u ON u."Id"=p."SubjectId"
              WHERE p."SubjectId"=@subjectId AND p."SoftwareId"=s."SoftwareId" AND p."Operation"='enrollment.manage' AND u."IsEnabled" AND NOT u."MustChangePassword")
            ORDER BY c."Id" LIMIT @take
            """,new { instanceId,subjectId=scope.SubjectId,after,take=size+1 },token);
        var items=rows.Take(size).Select(x=>x.View()).ToArray(); return new(items,rows.Count>size?items[^1].Id:null);
    }
    private sealed class CredentialRow
    {
        public Guid Id {get;set;} public Guid SubjectId {get;set;} public DateTimeOffset? ExpiresAt {get;set;} public DateTimeOffset? RevokedAt {get;set;} public long Revision {get;set;}
        internal CredentialView View()=>new(Id,SubjectId,ExpiresAt,RevokedAt,Revision);
    }
    private sealed class HistoryRow
    {
        public Guid Id {get;set;} public Guid InstanceId {get;set;} public string InstallationState {get;set;}=""; public Guid? InstalledReleaseId {get;set;} public string? InstalledVersion {get;set;}
        public DateTimeOffset? InstalledAt {get;set;} public DateTimeOffset ReceivedAt {get;set;} public string ReportedRunningState {get;set;}="";
        internal InstallationHistoryView View()=>new(Id,InstanceId,InstallationState,InstalledReleaseId,InstalledVersion,InstalledAt,ReceivedAt,ReportedRunningState);
    }
    private sealed class GrantRow
    { public Guid Id {get;set;} public Guid SoftwareId {get;set;} public Guid[] DeviceIds {get;set;}=[]; public DateTimeOffset ExpiresAt {get;set;} public int MaxInstances {get;set;} public int UsedCount {get;set;} public DateTimeOffset? RevokedAt {get;set;} public long Revision {get;set;} }
}
internal class InstanceRow
{
    public Guid Id {get;set;} public Guid SoftwareId {get;set;} public Guid DeviceId {get;set;}
    public string DeviceNo {get;set;}=""; public string DeviceName {get;set;}=""; public Guid ProcessId {get;set;}
    public string ProcessCode {get;set;}=""; public string ProcessName {get;set;}=""; public string Lifecycle {get;set;}="";
    public long Revision {get;set;} public string? SnapshotJson {get;set;} public DateTimeOffset? LastAcceptedAt {get;set;}
    internal InstanceView View(SiteView site,DateTimeOffset now) => new(Id,SoftwareId,DeviceId,DeviceNo,DeviceName,new(site.SiteId,site.SiteName,ProcessId,ProcessCode,ProcessName),
        Lifecycle,SnapshotJson is null?null:JsonSerializer.Deserialize<StateReport>(SnapshotJson),LastAcceptedAt,
        LastAcceptedAt is { } t ? Math.Max(0,(long)Math.Floor((now-t).TotalSeconds)):null,InstanceValidation.Freshness(LastAcceptedAt,now),null,null,null,Revision);
}
