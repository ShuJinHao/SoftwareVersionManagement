using System.Text.Json;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Dapper;

internal sealed class RoleQueries(ReadQuerySession session) : IRoleQueries
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string RoleProjection = """
        SELECT r."Id",r."Name",r."IsEnabled",r."Revision",
          (SELECT count(DISTINCT a."SubjectId")::int FROM iam.role_assignments a WHERE a."RoleId"=r."Id") AS "AssignedPeople",
          COALESCE((SELECT jsonb_agg(o."Operation" ORDER BY o."Operation" COLLATE "C") FROM iam.role_operations o WHERE o."RoleId"=r."Id"),'[]'::jsonb)::text AS "Operations"
        FROM iam.roles r
        """;
    public async Task<RoleView?> RoleAsync(Guid id, CancellationToken token) =>
        (await session.QueryAsync<RoleRow>(RoleProjection + " WHERE r.\"Id\"=@id", new { id }, token)).Select(r => r.View()).SingleOrDefault();
    public async Task<RolePage> ListAsync(string? name, Guid? afterId, int pageSize, CancellationToken token)
    {
        var rows = await session.QueryAsync<RoleRow>(RoleProjection + """
             WHERE (CAST(@name AS text) IS NULL OR r."Name" ILIKE @name ESCAPE '\')
             AND (CAST(@afterId AS uuid) IS NULL OR r."Id">@afterId) ORDER BY r."Id" LIMIT @take
            """, new { name = CatalogReadScope.Contains(name), afterId, take = pageSize + 1 }, token);
        var items = rows.Take(pageSize).Select(r => r.View()).ToArray(); return new(items, rows.Count > pageSize ? items[^1].Id : null);
    }
    public async Task<AuthorizationView> GetAsync(Guid subjectId, CancellationToken token)
    {
        var row = (await session.QueryAsync<AuthorizationRow>("""
            SELECT g."Revision",
              COALESCE((SELECT jsonb_object_agg(s."Id",s."Name") FROM rel.software s WHERE EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=g."SubjectId" AND p."SoftwareId"=s."Id")),'{}'::jsonb)::text AS "SoftwareNames",
              COALESCE((SELECT jsonb_agg(jsonb_build_object('softwareId',p."SoftwareId",'operation',p."Operation") ORDER BY p."Operation",p."SoftwareId") FROM iam.permissions p WHERE p."SubjectId"=g."SubjectId"),'[]'::jsonb)::text AS "DirectPermissions",
              COALESCE((SELECT jsonb_agg(jsonb_build_object('roleId',r."Id",'name',r."Name",'isEnabled',r."IsEnabled",'revision',r."Revision",
                'operations',COALESCE((SELECT jsonb_agg(o."Operation" ORDER BY o."Operation") FROM iam.role_operations o WHERE o."RoleId"=r."Id"),'[]'::jsonb),
                'scopes',(SELECT jsonb_agg(jsonb_build_object('kind',a."ScopeKind",'resourceId',a."ResourceId",'name',CASE a."ScopeKind" WHEN 'Site' THEN '本厂全部现场' WHEN 'Process' THEN pr."Name" WHEN 'Device' THEN d."Name" WHEN 'Software' THEN s."Name" END) ORDER BY a."ScopeKind",a."ResourceId")
                  FROM iam.role_assignments a LEFT JOIN ins.processes pr ON a."ScopeKind"='Process' AND pr."Id"=a."ResourceId"
                  LEFT JOIN ins.devices d ON a."ScopeKind"='Device' AND d."Id"=a."ResourceId" LEFT JOIN rel.software s ON a."ScopeKind"='Software' AND s."Id"=a."ResourceId"
                  WHERE a."SubjectId"=g."SubjectId" AND a."RoleId"=r."Id")) ORDER BY r."Id")
                FROM iam.roles r WHERE EXISTS(SELECT 1 FROM iam.role_assignments a WHERE a."SubjectId"=g."SubjectId" AND a."RoleId"=r."Id")),'[]'::jsonb)::text AS "Roles"
            FROM iam.subject_guards g WHERE g."SubjectId"=@subjectId
            """, new { subjectId }, token)).SingleOrDefault() ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        return new(subjectId, row.Revision, JsonSerializer.Deserialize<PermissionView[]>(row.DirectPermissions, Json)!, JsonSerializer.Deserialize<RoleAssignmentView[]>(row.Roles, Json)!)
        { SoftwareNames = JsonSerializer.Deserialize<Dictionary<Guid,string>>(row.SoftwareNames, Json)! };
    }
    private sealed class AuthorizationRow { public long Revision { get; set; } public string DirectPermissions { get; set; } = "[]"; public string Roles { get; set; } = "[]"; public string SoftwareNames { get; set; } = "{}"; }
    private sealed class RoleRow
    {
        public Guid Id { get; set; } public string Name { get; set; } = ""; public bool IsEnabled { get; set; } public long Revision { get; set; }
        public int AssignedPeople { get; set; } public string Operations { get; set; } = "[]";
        internal RoleView View() => new(Id, Name, IsEnabled, JsonSerializer.Deserialize<string[]>(Operations, Json)!, AssignedPeople, Revision);
    }
}
