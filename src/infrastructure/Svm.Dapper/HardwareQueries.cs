using System.Text.Json;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Tasks;

namespace Svm.Dapper;

internal sealed class HardwareQueries(ReadQuerySession session, CatalogReadScope scope, TimeProvider? clock = null,
    IFormalReleaseAvailability? availability = null, ITaskQueries? tasks = null, Svm.Services.Contracts.Plc.IPlcQueries? plc = null) : IHardwareQueries
{
    private const string HardwareProjection = """
        SELECT h."Id",h."DeviceId",h."Type",h."Name",h."AssetNumber",h."ManagementIp",h."Revision"
        FROM ins.hardware h JOIN ins.devices d ON d."Id"=h."DeviceId" JOIN ins.processes pr ON pr."Id"=d."ProcessId"
        """;
    public async Task<HardwareView?> GetAsync(Guid id, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        return (await session.QueryAsync<HardwareView>(HardwareProjection + " WHERE h.\"Id\"=@id AND pr.\"SiteId\"=@siteId AND iam.has_field(@subjectId,'asset.read',d.\"ProcessId\",d.\"Id\",NULL)", new { id, siteId = scope.Site.SiteId, subjectId = scope.SubjectId }, token)).SingleOrDefault();
    }
    public async Task<CatalogPage<HardwareView>> ListAsync(Guid deviceId, HardwareListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        var rows = await session.QueryAsync<HardwareView>(HardwareProjection + """
             WHERE h."DeviceId"=@deviceId AND pr."SiteId"=@siteId AND iam.has_field(@subjectId,'asset.read',d."ProcessId",d."Id",NULL)
             AND (CAST(@name AS text) IS NULL OR h."Name" ILIKE @name ESCAPE '\')
             AND (CAST(@afterId AS uuid) IS NULL OR h."Id">@afterId) ORDER BY h."Id" LIMIT @take
            """, new { deviceId, siteId = scope.Site.SiteId, subjectId = scope.SubjectId, name = CatalogReadScope.Contains(input.Filter.Name), afterId = input.After?.Id, take = input.PageSize + 1 }, token);
        return CatalogReadScope.Page(rows, input.PageSize, x => new(x.Id.ToString("D"), x.Id));
    }
    public async Task<CatalogPage<HardwareBindingView>> BindingsAsync(Guid hardwareId, HardwareListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        var rows = await session.QueryAsync<HardwareBindingView>("""
            SELECT b."Id",b."HardwareId",h."DeviceId",b."SoftwareId",b."IsActive",
              CASE WHEN iam.has_field(@subjectId,'instance.read',d."ProcessId",d."Id",b."SoftwareId") THEN b."InstanceId" ELSE NULL END AS "InstanceId",b."Revision"
            FROM ins.hardware_software_bindings b JOIN ins.hardware h ON h."Id"=b."HardwareId"
            JOIN ins.devices d ON d."Id"=h."DeviceId" JOIN ins.processes pr ON pr."Id"=d."ProcessId"
            WHERE b."HardwareId"=@hardwareId AND b."IsActive" AND pr."SiteId"=@siteId AND iam.has_field(@subjectId,'asset.read',d."ProcessId",d."Id",NULL)
              AND iam.has_field(@subjectId,'software.read',d."ProcessId",d."Id",b."SoftwareId")
              AND (CAST(@afterId AS uuid) IS NULL OR b."Id">@afterId) ORDER BY b."Id" LIMIT @take
            """, new { hardwareId, siteId = scope.Site.SiteId, subjectId = scope.SubjectId, afterId = input.After?.Id, take = input.PageSize + 1 }, token);
        return CatalogReadScope.Page(rows, input.PageSize, x => new(x.Id.ToString("D"), x.Id));
    }
    // One row per physical installation position, unassigned historical instance, or legacy device mapping awaiting a position.
    // Every consumer of these rows (including counts) applies the same software and instance visibility.
    private const string VisiblePositions = """
        WITH positions AS (
          SELECT b."Id" AS "RowId",b."Id" AS "PositionId",b."HardwareId",h."DeviceId",b."SoftwareId",b."InstanceId",b."Revision" AS "PositionRevision"
          FROM ins.hardware_software_bindings b JOIN ins.hardware h ON h."Id"=b."HardwareId"
          JOIN ins.device_software_bindings parent ON parent."Id"=b."DeviceBindingId" WHERE b."IsActive" AND parent."IsActive"
          UNION ALL
          SELECT i."Id",NULL::uuid,NULL::uuid,i."DeviceId",i."SoftwareId",i."Id",NULL::bigint FROM ins.instances i WHERE i."HardwareBindingId" IS NULL
          UNION ALL
          SELECT b."Id",NULL::uuid,NULL::uuid,b."DeviceId",b."SoftwareId",NULL::uuid,NULL::bigint
          FROM ins.device_software_bindings b WHERE b."IsActive"
            AND NOT EXISTS(SELECT 1 FROM ins.hardware_software_bindings h WHERE h."DeviceBindingId"=b."Id" AND h."IsActive")
            AND NOT EXISTS(SELECT 1 FROM ins.instances i WHERE i."DeviceId"=b."DeviceId" AND i."SoftwareId"=b."SoftwareId")
        ), visible AS (
          SELECT p.* FROM positions p JOIN rel.software s ON s."Id"=p."SoftwareId" JOIN ins.devices vd ON vd."Id"=p."DeviceId"
          WHERE iam.has_field(@subjectId,'software.read',vd."ProcessId",vd."Id",p."SoftwareId")
            AND (s."Category"='PLC' OR iam.has_field(@subjectId,'instance.read',vd."ProcessId",vd."Id",p."SoftwareId"))
            AND iam.has_field(@subjectId,'asset.read',vd."ProcessId",vd."Id",NULL)
            AND (CAST(@softwareId AS uuid) IS NULL OR p."SoftwareId"=@softwareId)
            AND (CAST(@hardwareId AS uuid) IS NULL OR p."HardwareId"=@hardwareId)
            AND (CAST(@category AS text) IS NULL OR s."Category"=@category)
        )
        """;
    private const string DevicesFrom = """
         FROM ins.devices d JOIN ins.processes pr ON pr."Id"=d."ProcessId"
         WHERE pr."SiteId"=@siteId AND iam.has_field(@subjectId,'asset.read',d."ProcessId",d."Id",NULL) AND (CAST(@processId AS uuid) IS NULL OR pr."Id"=@processId)
           AND (CAST(@deviceId AS uuid) IS NULL OR d."Id"=@deviceId)
           AND (CAST(@name AS text) IS NULL OR d."Name" ILIKE @name ESCAPE '\' OR d."DeviceNo" ILIKE @name ESCAPE '\')
           AND ((CAST(@softwareId AS uuid) IS NULL AND CAST(@hardwareId AS uuid) IS NULL AND CAST(@category AS text) IS NULL)
             OR EXISTS(SELECT 1 FROM visible v WHERE v."DeviceId"=d."Id"))
        """;
    public async Task<WorkspacePage<WorkspaceDeviceView>> DevicesAsync(HardwareListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token); var parameters = Parameters(input);
        var count = (await session.QueryAsync<long>(VisiblePositions + " SELECT COUNT(*)" + DevicesFrom, parameters, token)).Single();
        var rows = await session.QueryAsync<DeviceRow>(VisiblePositions + """
            SELECT d."Id",d."DeviceNo",d."Name",d."AssetNumber",d."Revision",pr."Id" AS "ProcessId",pr."Code" AS "ProcessCode",pr."Name" AS "ProcessName",
              (SELECT COUNT(*)::int FROM ins.hardware h WHERE h."DeviceId"=d."Id") AS "HardwareCount",
              (SELECT COUNT(*)::int FROM visible v WHERE v."DeviceId"=d."Id") AS "InstallationCount",
              (SELECT COUNT(*)::int FROM visible v WHERE v."DeviceId"=d."Id" AND v."PositionId" IS NULL) AS "PendingLocationCount",
              (SELECT COUNT(*)::int FROM visible v JOIN ins.instance_snapshots s ON s."InstanceId"=v."InstanceId" WHERE v."DeviceId"=d."Id" AND s."LastAcceptedAt" IS NULL) AS "NeverReportedCount",
              (SELECT COUNT(*)::int FROM visible v JOIN ins.instance_snapshots s ON s."InstanceId"=v."InstanceId" WHERE v."DeviceId"=d."Id" AND s."LastAcceptedAt"<@boundary) AS "UnknownCount"
            """ + DevicesFrom + """
             AND (CAST(@afterId AS uuid) IS NULL OR (d."DeviceNo" COLLATE "C",d."Id")>(CAST(@afterKey AS text) COLLATE "C",CAST(@afterId AS uuid)))
             ORDER BY d."DeviceNo" COLLATE "C",d."Id" LIMIT @take
            """, parameters, token);
        var page = CatalogReadScope.Page(rows, input.PageSize, x => new(x.DeviceNo, x.Id));
        return new(page.Items.Select(x => new WorkspaceDeviceView(x.View(scope.Site), x.HardwareCount, x.InstallationCount, x.PendingLocationCount, x.NeverReportedCount, x.UnknownCount)).ToArray(), page.Next, count);
    }
    private const string InstallationsFrom = """
         FROM visible v JOIN ins.devices d ON d."Id"=v."DeviceId" JOIN ins.processes pr ON pr."Id"=d."ProcessId"
         JOIN rel.software s ON s."Id"=v."SoftwareId" LEFT JOIN ins.hardware h ON h."Id"=v."HardwareId"
         LEFT JOIN ins.instances i ON i."Id"=v."InstanceId" LEFT JOIN ins.instance_snapshots ss ON ss."InstanceId"=i."Id"
         WHERE pr."SiteId"=@siteId AND iam.has_field(@subjectId,'asset.read',d."ProcessId",d."Id",NULL) AND (CAST(@processId AS uuid) IS NULL OR pr."Id"=@processId)
           AND (CAST(@deviceId AS uuid) IS NULL OR d."Id"=@deviceId)
           AND (CAST(@name AS text) IS NULL OR d."Name" ILIKE @name ESCAPE '\' OR d."DeviceNo" ILIKE @name ESCAPE '\' OR s."Name" ILIKE @name ESCAPE '\' OR h."Name" ILIKE @name ESCAPE '\')
        """;
    public async Task<WorkspacePage<WorkspaceInstallationView>> InstallationsAsync(HardwareListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token); var parameters = Parameters(input);
        var total = (await session.QueryAsync<long>(VisiblePositions + " SELECT COUNT(*)" + InstallationsFrom, parameters, token)).Single();
        var rows = await session.QueryAsync<PositionRow>(VisiblePositions + """
            SELECT v."RowId",v."PositionId",v."PositionRevision",v."DeviceId",v."SoftwareId",v."InstanceId",
              h."Id" AS "HardwareId",h."Type" AS "HardwareType",h."Name" AS "HardwareName",h."AssetNumber" AS "HardwareAssetNumber",h."ManagementIp",h."Revision" AS "HardwareRevision",
              d."DeviceNo",d."Name" AS "DeviceName",d."AssetNumber" AS "DeviceAssetNumber",d."Revision" AS "DeviceRevision",pr."Id" AS "ProcessId",pr."Code" AS "ProcessCode",pr."Name" AS "ProcessName",
              s."Code" AS "SoftwareCode",s."Name" AS "SoftwareName",s."Category",s."Description",s."Revision" AS "SoftwareRevision",
              i."Id",i."Lifecycle",i."Revision",ss."SnapshotJson"::text AS "SnapshotJson",ss."LastAcceptedAt"
            """ + InstallationsFrom + """
             AND (CAST(@afterId AS uuid) IS NULL OR (s."Code" COLLATE "C",v."RowId")>(CAST(@afterKey AS text) COLLATE "C",CAST(@afterId AS uuid)))
             ORDER BY s."Code" COLLATE "C",v."RowId" LIMIT @take
            """, parameters, token);
        var selected = rows.Take(input.PageSize).ToArray();
        if (availability is not null)
        { var latest = await availability.LatestAsync(selected.Select(x => x.SoftwareId).Distinct().ToArray(), token); foreach (var row in selected) row.LatestAvailableFormalReleaseId = latest.TryGetValue(row.SoftwareId, out var id) ? id : null; }
        if (tasks is not null)
        { var summaries = await tasks.LatestAsync(selected.Where(x => x.InstanceId is not null).Select(x => x.InstanceId!.Value).ToArray(), token); foreach (var row in selected) if (summaries.TryGetValue(row.Id, out var summary)) { row.LatestTaskId = summary.TaskId; row.LatestTaskResult = summary.Result; } }
        var usage = plc is null ? new Dictionary<Guid,Svm.Services.Contracts.Plc.PlcUsageSummary>() : await plc.CurrentAsync(selected.Where(x=>x.Category=="PLC" && x.PositionId!=null).Select(x=>x.PositionId!.Value).ToArray(),token);
        var versions = await session.QueryAsync<VersionRow>("SELECT \"Id\",(\"Major\"::text || '.' || \"Minor\"::text || '.' || \"Patch\"::text) AS \"Version\" FROM rel.releases WHERE \"Id\"=ANY(@ids)",new { ids=selected.Where(x=>x.LatestAvailableFormalReleaseId!=null).Select(x=>x.LatestAvailableFormalReleaseId!.Value).Distinct().ToArray() },token);
        var now = (clock ?? TimeProvider.System).GetUtcNow(); var site = scope.Site;
        var items = selected.Select(x =>
        {
            x.HardwareBindingId = x.PositionId;
            HardwareView? hardware = x.HardwareId is { } hid ? new(hid, x.DeviceId, x.HardwareType!, x.HardwareName!, x.HardwareAssetNumber, x.ManagementIp, x.HardwareRevision!.Value) : null;
            HardwareBindingView? position = x.PositionId is { } pid ? new(pid, x.HardwareId!.Value, x.DeviceId, x.SoftwareId, true, x.InstanceId, x.PositionRevision!.Value) : null;
            return new WorkspaceInstallationView(x.RowId, new(x.DeviceId, x.DeviceNo, x.DeviceName, x.ProcessId, x.ProcessCode, x.ProcessName, site.SiteId, site.SiteName, x.DeviceRevision, x.DeviceAssetNumber), hardware, position,
                new(x.SoftwareId, x.SoftwareCode, x.SoftwareName, x.Category, x.Description, x.LatestAvailableFormalReleaseId, x.SoftwareRevision),
                x.InstanceId is null ? null : x.View(site, now), x.PositionId is null ? "PendingHardware" : x.Category=="PLC" ? usage.GetValueOrDefault(x.PositionId.Value)?.Current is null ? "PendingConfirmation" : "Confirmed" : x.InstanceId is null ? "NotRegistered" : "Registered")
            { PlcUsage=x.PositionId is {} key?usage.GetValueOrDefault(key):null, LatestFormalVersion=versions.FirstOrDefault(v=>v.Id==x.LatestAvailableFormalReleaseId)?.Version };
        }).ToArray();
        return new(items, rows.Count > input.PageSize ? new(selected[^1].SoftwareCode, selected[^1].RowId) : null, total);
    }
    private object Parameters(HardwareListInput x) => new { siteId = scope.Site.SiteId, subjectId = scope.SubjectId, x.Filter.ProcessId, x.Filter.DeviceId, x.Filter.HardwareId, x.Filter.SoftwareId,
        name = CatalogReadScope.Contains(x.Filter.Name), x.Filter.Category, afterId = x.After?.Id, afterKey = x.After?.SortKey, take = x.PageSize + 1, boundary = (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(-5) };
    private sealed class DeviceRow
    {
        public Guid Id {get;set;} public string DeviceNo {get;set;}=""; public string Name {get;set;}=""; public string? AssetNumber {get;set;} public long Revision {get;set;}
        public Guid ProcessId {get;set;} public string ProcessCode {get;set;}=""; public string ProcessName {get;set;}="";
        public int HardwareCount {get;set;} public int InstallationCount {get;set;} public int PendingLocationCount {get;set;} public int NeverReportedCount {get;set;} public int UnknownCount {get;set;}
        internal DeviceView View(SiteView site) => new(Id,DeviceNo,Name,ProcessId,ProcessCode,ProcessName,site.SiteId,site.SiteName,Revision,AssetNumber);
    }
    private sealed record VersionRow(Guid Id,string Version);
    private sealed class PositionRow : InstanceRow
    {
        public Guid RowId {get;set;} public Guid? PositionId {get;set;} public long? PositionRevision {get;set;} public Guid? InstanceId {get;set;}
        public string? HardwareType {get;set;} public string? HardwareAssetNumber {get;set;} public long? HardwareRevision {get;set;}
        public string? DeviceAssetNumber {get;set;} public long DeviceRevision {get;set;}
        public string SoftwareCode {get;set;}=""; public string SoftwareName {get;set;}=""; public string Category {get;set;}=""; public string? Description {get;set;} public long SoftwareRevision {get;set;}
    }
}
