using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Dapper;

public static class CatalogQueryRegistration
{
    public static IServiceCollection AddSvmCatalogQueries(this IServiceCollection services)
    {
        services.AddScoped<CatalogReadScope>();
        services.AddScoped<ISoftwareCatalogQueries, SoftwareCatalogQueries>();
        services.AddScoped<ISiteAssetQueries, SiteAssetQueries>(); return services;
    }
}
internal sealed class CatalogReadScope(ReadQuerySession session, SiteCatalogOptions options, ICallContext calls)
{
    internal Guid SubjectId => calls.Current?.Actor.ActorId ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
    internal SiteView Site => options.Require();
    internal async Task EnsureAsync(string? globalPermission, CancellationToken token)
    {
        var site = Site;
        var ids = await session.QueryAsync<Guid>("SELECT \"SiteId\" FROM ins.site_identity", null, token);
        if (ids.Count > 1 || ids.Any(id => id != site.SiteId)) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var allowed = (await session.QueryAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."Id"=@subjectId AND u."IsEnabled" AND NOT u."MustChangePassword"
              AND (CAST(@permission AS text) IS NULL OR EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=u."Id"
                   AND p."SoftwareId" IS NULL AND p."Operation"=@permission)))
            """, new { subjectId = SubjectId, permission = globalPermission }, token)).Single();
        if (!allowed) throw new RequestRejectedException(RequestFailure.PermissionDenied);
    }
    internal static string? Prefix(string? text) => text is null ? null : Escape(text) + "%";
    internal static string? Contains(string? text) => text is null ? null : "%" + Escape(text) + "%";
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    internal static CatalogPage<T> Page<T>(IReadOnlyList<T> rows, int size, Func<T, CatalogPosition> position)
    { var items = rows.Take(size).ToArray(); return new(items, rows.Count > size ? position(items[^1]) : null); }
}
internal sealed class SoftwareCatalogQueries(ReadQuerySession session, CatalogReadScope scope) : ISoftwareCatalogQueries
{
    private const string Projection = """
        SELECT s."Id",s."Code",s."Name",s."Category",s."Description",NULL::uuid AS "LatestAvailableFormalReleaseId",s."Revision"
        FROM rel.software s
        """;
    private const string Filter = """
        (CAST(@code AS text) IS NULL OR s."Code" LIKE @code ESCAPE '\') AND (CAST(@name AS text) IS NULL OR s."Name" ILIKE @name ESCAPE '\')
        AND (CAST(@category AS text) IS NULL OR s."Category"=@category)
        AND (CAST(@afterId AS uuid) IS NULL OR (s."Code" COLLATE "C",s."Id")>(CAST(@afterKey AS text) COLLATE "C",CAST(@afterId AS uuid)))
        """;
    private const string Visible = "EXISTS(SELECT 1 FROM iam.permissions p WHERE p.\"SubjectId\"=@subjectId AND p.\"SoftwareId\"=s.\"Id\" AND p.\"Operation\"='software.read')";
    public async Task<SoftwareView?> GetAsync(Guid id, CancellationToken token)
    {
        await scope.EnsureAsync(null, token);
        return (await session.QueryAsync<SoftwareView>(Projection + " WHERE s.\"Id\"=@id AND " + Visible, new { id, subjectId = scope.SubjectId }, token)).SingleOrDefault();
    }
    public async Task<CatalogPage<SoftwareView>> ListAsync(CatalogListInput input, CancellationToken token)
    {
        await scope.EnsureAsync(null, token);
        var rows = await session.QueryAsync<SoftwareView>(Projection + " WHERE " + Visible + " AND " + Filter + " ORDER BY s.\"Code\" COLLATE \"C\",s.\"Id\" LIMIT @take", Parameters(input), token);
        return CatalogReadScope.Page(rows, input.PageSize, x => new(x.Code, x.Id));
    }
    public async Task<PermissionOptionsPage> PermissionOptionsAsync(CatalogListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("identity.manage", token);
        var rows = await session.QueryAsync<SoftwareView>(Projection.Replace("s.\"Description\"", "NULL::text AS \"Description\"") + " WHERE " + Filter + " ORDER BY s.\"Code\" COLLATE \"C\",s.\"Id\" LIMIT @take", Parameters(input), token);
        var page = CatalogReadScope.Page(rows, input.PageSize, x => new(x.Code, x.Id));
        return new(page.Items, PermissionCatalog.Entries.Where(p => !p.Global).Select(p => p.Operation).Order(StringComparer.Ordinal).ToArray(), page.Next);
    }
    private object Parameters(CatalogListInput x) => new { subjectId = scope.SubjectId, code = CatalogReadScope.Prefix(x.Filter.Code), name = CatalogReadScope.Contains(x.Filter.Name),
        category = x.Filter.Category, afterKey = x.After?.SortKey, afterId = x.After?.Id, take = x.PageSize + 1 };
}
internal sealed class SiteAssetQueries(ReadQuerySession session, CatalogReadScope scope) : ISiteAssetQueries
{
    private const string ProcessProjection = "SELECT p.\"Id\",p.\"SiteId\",p.\"Code\",p.\"Name\",p.\"Revision\" FROM ins.processes p";
    private const string DeviceProjection = """
        SELECT d."Id",d."DeviceNo",d."Name",d."ProcessId",p."Code" AS "ProcessCode",p."Name" AS "ProcessName",p."SiteId",@siteName AS "SiteName",d."Revision"
        FROM ins.devices d JOIN ins.processes p ON p."Id"=d."ProcessId"
        """;
    public async Task<ProcessView?> ProcessAsync(Guid id, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        return (await session.QueryAsync<ProcessView>(ProcessProjection + " WHERE p.\"Id\"=@id AND p.\"SiteId\"=@siteId", new { id, siteId = scope.Site.SiteId }, token)).SingleOrDefault();
    }
    public async Task<CatalogPage<ProcessView>> ProcessesAsync(CatalogListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        var rows = await session.QueryAsync<ProcessView>(ProcessProjection + """
             WHERE p."SiteId"=@siteId AND (CAST(@code AS text) IS NULL OR p."Code" LIKE @code ESCAPE '\')
             AND (CAST(@name AS text) IS NULL OR p."Name" ILIKE @name ESCAPE '\')
             AND (CAST(@afterId AS uuid) IS NULL OR (p."Code" COLLATE "C",p."Id")>(CAST(@afterKey AS text) COLLATE "C",CAST(@afterId AS uuid)))
             ORDER BY p."Code" COLLATE "C",p."Id" LIMIT @take
            """, Parameters(input), token);
        return CatalogReadScope.Page(rows, input.PageSize, x => new(x.Code, x.Id));
    }
    public async Task<DeviceView?> DeviceAsync(Guid id, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        return (await session.QueryAsync<DeviceView>(DeviceProjection + " WHERE d.\"Id\"=@id AND p.\"SiteId\"=@siteId", new { id, siteId = scope.Site.SiteId, siteName = scope.Site.SiteName }, token)).SingleOrDefault();
    }
    public async Task<CatalogPage<DeviceView>> DevicesAsync(CatalogListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        var rows = await session.QueryAsync<DeviceView>(DeviceProjection + """
             WHERE p."SiteId"=@siteId AND (CAST(@processId AS uuid) IS NULL OR d."ProcessId"=@processId)
             AND (CAST(@code AS text) IS NULL OR d."DeviceNo" LIKE @code ESCAPE '\')
             AND (CAST(@name AS text) IS NULL OR d."Name" ILIKE @name ESCAPE '\')
             AND (CAST(@afterId AS uuid) IS NULL OR (d."DeviceNo" COLLATE "C",d."Id")>(CAST(@afterKey AS text) COLLATE "C",CAST(@afterId AS uuid)))
             ORDER BY d."DeviceNo" COLLATE "C",d."Id" LIMIT @take
            """, Parameters(input), token);
        return CatalogReadScope.Page(rows, input.PageSize, x => new(x.DeviceNo, x.Id));
    }
    private const string BoundProjection = """
        SELECT b."Id",b."DeviceId",b."SoftwareId",b."Revision",b."IsActive",
          s."Code",s."Name",s."Category",s."Description",s."Revision" AS "SoftwareRevision"
        FROM ins.device_software_bindings b JOIN ins.devices d ON d."Id"=b."DeviceId"
        JOIN ins.processes pr ON pr."Id"=d."ProcessId" JOIN rel.software s ON s."Id"=b."SoftwareId"
        """;
    private const string BoundFilter = """
        b."DeviceId"=@deviceId AND b."IsActive" AND pr."SiteId"=@siteId
        AND EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=@subjectId AND p."SoftwareId"=s."Id" AND p."Operation"='software.read')
        AND (CAST(@softwareId AS uuid) IS NULL OR s."Id"=@softwareId)
        AND (CAST(@category AS text) IS NULL OR s."Category"=@category)
        AND (CAST(@afterId AS uuid) IS NULL OR (s."Code" COLLATE "C",b."Id")>(CAST(@afterKey AS text) COLLATE "C",CAST(@afterId AS uuid)))
        """;
    public async Task<CatalogPage<BindingView>> BindingsAsync(Guid deviceId, CatalogListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        var rows = await BoundAsync(deviceId, input, false, token);
        var page = CatalogReadScope.Page(rows, input.PageSize, x => new(x.Code, x.Id));
        return new(page.Items.Select(Binding).ToArray(), page.Next);
    }
    public async Task<CatalogPage<DeviceSoftwareInventoryItem>> InventoryAsync(Guid deviceId, CatalogListInput input, CancellationToken token)
    {
        await scope.EnsureAsync("asset.read", token);
        var page = CatalogReadScope.Page(await BoundAsync(deviceId, input, true, token), input.PageSize, x => new(x.Code, x.Id));
        return new(page.Items.Select(x => new DeviceSoftwareInventoryItem(new(x.SoftwareId, x.Code, x.Name, x.Category, x.Description, null, x.SoftwareRevision), Binding(x))).ToArray(), page.Next);
    }
    private Task<IReadOnlyList<BoundRow>> BoundAsync(Guid deviceId, CatalogListInput input, bool inventory, CancellationToken token) =>
        session.QueryAsync<BoundRow>(BoundProjection + " WHERE " + BoundFilter + (inventory ? " AND EXISTS(SELECT 1 FROM iam.permissions ip WHERE ip.\"SubjectId\"=@subjectId AND ip.\"SoftwareId\"=s.\"Id\" AND ip.\"Operation\"='instance.read')" : "") + " ORDER BY s.\"Code\" COLLATE \"C\",b.\"Id\" LIMIT @take",
            new { deviceId, subjectId = scope.SubjectId, siteId = scope.Site.SiteId, category = input.Filter.Category, softwareId = input.Filter.SoftwareId,
                afterKey = input.After?.SortKey, afterId = input.After?.Id, take = input.PageSize + 1 }, token);
    private object Parameters(CatalogListInput x) => new { subjectId = scope.SubjectId, siteId = scope.Site.SiteId, siteName = scope.Site.SiteName,
        code = CatalogReadScope.Prefix(x.Filter.Code), name = CatalogReadScope.Contains(x.Filter.Name), processId = x.Filter.ProcessId,
        afterKey = x.After?.SortKey, afterId = x.After?.Id, take = x.PageSize + 1 };
    private static BindingView Binding(BoundRow x) => new(x.Id, x.DeviceId, x.SoftwareId, x.Revision, x.IsActive);
    private sealed class BoundRow
    {
        public Guid Id { get; set; } public Guid DeviceId { get; set; } public Guid SoftwareId { get; set; }
        public long Revision { get; set; } public bool IsActive { get; set; } public long SoftwareRevision { get; set; }
        public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string Category { get; set; } = ""; public string? Description { get; set; }
    }
}
