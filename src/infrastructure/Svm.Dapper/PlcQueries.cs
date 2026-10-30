using System.Text.Json;
using Svm.Services.Contracts.Plc;
namespace Svm.Dapper;

internal sealed class PlcQueries(ReadQuerySession session,CatalogReadScope scope) : IPlcQueries
{
    public async Task<PlcPage<PlcCredentialView>> CredentialsAsync(Guid softwareId,int size,Guid? after,CancellationToken token)
    {
        await scope.EnsureAsync(null,token);
        var rows=await session.QueryAsync<CredentialRow>("""
            SELECT c."Id",c."SoftwareId",c."Name",c."ScopeJson"::text AS "ScopeJson",c."ExpiresAt",c."RevokedAt",c."CreatedBy",c."CreatedAt",c."Revision"
            FROM iam.plc_upload_credentials c WHERE c."SoftwareId"=@softwareId
              AND iam.has_product(@subject,'plc.uploadCredential.manage',c."SoftwareId")
              AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(c."ScopeJson") s
                JOIN ins.hardware_software_bindings b ON b."Id"=(s->>'BindingId')::uuid
                JOIN ins.hardware h ON h."Id"=b."HardwareId" JOIN ins.devices d ON d."Id"=h."DeviceId"
                WHERE NOT iam.has_field(@subject,'asset.read',d."ProcessId",d."Id",NULL))
              AND (CAST(@after AS uuid) IS NULL OR c."Id">@after) ORDER BY c."Id" LIMIT @take
            """,new { softwareId,subject=scope.SubjectId,after,take=size+1 },token);
        var selected=rows.Take(size).Select(c=>new PlcCredentialView(c.Id,c.SoftwareId,c.Name,JsonSerializer.Deserialize<PlcScope[]>(c.ScopeJson)!,c.ExpiresAt,c.RevokedAt,c.CreatedBy,c.CreatedAt,c.Revision)).ToArray();
        return new(selected,rows.Count>size?selected[^1].Id:null);
    }
    private const string Projection="""
        SELECT u."Id",u."HardwareId",u."BindingId",u."BindingRevision",u."SoftwareId",u."ReleaseId",u."Version",r."State" AS "ReleaseState",
        u."PreviousReleaseId",u."FieldAt",u."RecordedAt",u."ActorId",u."EmployeeNo",u."Note",u."Materials",u."CorrectsId"
        FROM ins.plc_usage_records u JOIN rel.releases r ON r."Id"=u."ReleaseId"
        JOIN ins.hardware h ON h."Id"=u."HardwareId" JOIN ins.devices d ON d."Id"=h."DeviceId" JOIN ins.processes p ON p."Id"=d."ProcessId"
        """;
    private const string Visible="""
        p."SiteId"=@site AND iam.has_field(@subject,'asset.read',d."ProcessId",d."Id",NULL)
        AND iam.has_field(@subject,'software.read',d."ProcessId",d."Id",u."SoftwareId")
        """;
    public async Task<PlcPage<PlcUsageView>> UsageAsync(Guid hardwareId,int size,Guid? after,CancellationToken token)
    {
        await scope.EnsureAsync(null,token);
        var rows=await session.QueryAsync<UsageRow>(Projection+" WHERE "+Visible+" AND u.\"HardwareId\"=@hardwareId AND (CAST(@after AS uuid) IS NULL OR u.\"Id\">@after) ORDER BY u.\"Id\" LIMIT @take",
            new { hardwareId,subject=scope.SubjectId,site=scope.Site.SiteId,after,take=size+1 },token);
        var selected=rows.Take(size).Select(x=>x.View).ToArray();return new(selected,rows.Count>size?selected[^1].Id:null);
    }
    public async Task<IReadOnlyDictionary<Guid,PlcUsageSummary>> CurrentAsync(IReadOnlyList<Guid> bindings,CancellationToken token)
    {
        if(bindings.Count==0)return new Dictionary<Guid,PlcUsageSummary>();
        if(bindings.Count>200)throw new Svm.Services.Contracts.Framework.RequestRejectedException(Svm.Services.Contracts.Framework.RequestFailure.ValidationFailed);
        await scope.EnsureAsync(null,token);
        var states=await session.QueryAsync<StateRow>("""
            SELECT b."Id",COALESCE(s."Revision",0) AS "Revision",
              CASE WHEN s."BindingRevision"=b."Revision" AND b."IsActive" THEN s."CurrentRecordId" ELSE NULL END AS "CurrentRecordId"
            FROM ins.hardware_software_bindings b JOIN ins.hardware h ON h."Id"=b."HardwareId" JOIN ins.devices d ON d."Id"=h."DeviceId"
            JOIN ins.processes p ON p."Id"=d."ProcessId" LEFT JOIN ins.plc_usage_state s ON s."Id"=b."Id"
            WHERE b."Id"=ANY(@bindings) AND h."Type"='PLC' AND p."SiteId"=@site
              AND iam.has_field(@subject,'asset.read',d."ProcessId",d."Id",NULL)
              AND iam.has_field(@subject,'software.read',d."ProcessId",d."Id",b."SoftwareId")
            """,new {bindings=bindings.ToArray(),subject=scope.SubjectId,site=scope.Site.SiteId},token);
        var current=await session.QueryAsync<UsageRow>(Projection+" WHERE "+Visible+" AND u.\"Id\"=ANY(@ids)",
            new {ids=states.Where(s=>s.CurrentRecordId!=null).Select(s=>s.CurrentRecordId!.Value).ToArray(),subject=scope.SubjectId,site=scope.Site.SiteId},token);
        return states.ToDictionary(s=>s.Id,s=>new PlcUsageSummary(s.Revision,current.SingleOrDefault(c=>c.Id==s.CurrentRecordId)?.View));
    }
    private sealed record StateRow(Guid Id,long Revision,Guid? CurrentRecordId);
    private sealed class CredentialRow
    {
        public Guid Id {get;set;} public Guid SoftwareId {get;set;} public string Name {get;set;}=""; public string ScopeJson {get;set;}="[]";
        public DateTimeOffset ExpiresAt {get;set;} public DateTimeOffset? RevokedAt {get;set;} public Guid CreatedBy {get;set;} public DateTimeOffset CreatedAt {get;set;} public long Revision {get;set;}
    }
    private sealed class UsageRow
    {
        public Guid Id {get;set;} public Guid HardwareId {get;set;} public Guid BindingId {get;set;} public long BindingRevision {get;set;}
        public Guid SoftwareId {get;set;} public Guid ReleaseId {get;set;} public string Version {get;set;}=""; public string ReleaseState {get;set;}="";
        public Guid? PreviousReleaseId {get;set;} public DateTimeOffset FieldAt {get;set;} public DateTimeOffset RecordedAt {get;set;} public Guid ActorId {get;set;}
        public string EmployeeNo {get;set;}=""; public string Note {get;set;}=""; public string Materials {get;set;}=""; public Guid? CorrectsId {get;set;}
        public PlcUsageView View=>new(Id,HardwareId,BindingId,BindingRevision,SoftwareId,ReleaseId,Version,ReleaseState,PreviousReleaseId,FieldAt,RecordedAt,ActorId,EmployeeNo,Note,Materials,CorrectsId);
    }
}
