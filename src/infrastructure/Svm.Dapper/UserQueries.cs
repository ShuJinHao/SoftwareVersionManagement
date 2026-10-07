using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Identity;

namespace Svm.Dapper;

public static class UserQueryRegistration
{
    public static IServiceCollection AddSvmUserQueries(this IServiceCollection services) => services.AddScoped<IUserQueries, UserQueries>();
}

internal sealed class UserQueries(ReadQuerySession session) : IUserQueries
{
    private const string Projection = """
        SELECT u."Id",u."EmployeeNo",u."DisplayName",u."IsEnabled",u."MustChangePassword",g."Revision",
          COALESCE((SELECT jsonb_agg(jsonb_build_object('softwareId',p."SoftwareId",'operation',p."Operation")
                    ORDER BY p."Operation" COLLATE "C",p."SoftwareId")
                    FROM iam.permissions p WHERE p."SubjectId"=u."Id"),'[]'::jsonb)::text AS "PermissionsJson"
        FROM iam.users u JOIN iam.subject_guards g ON g."SubjectId"=u."Id"
        """;
    public async Task<UserView?> GetAsync(Guid userId, CancellationToken token) =>
        (await session.QueryAsync<Row>(Projection + " WHERE u.\"Id\"=@userId", new { userId }, token)).Select(View).SingleOrDefault();
    public async Task<UserPage> ListAsync(UserListInput input, CancellationToken token)
    {
        var rows = await session.QueryAsync<Row>(Projection + """
             WHERE (CAST(@employeeNo AS text) IS NULL OR u."EmployeeNo" LIKE CAST(@employeeNo AS text) ESCAPE '\')
             AND (CAST(@enabled AS boolean) IS NULL OR u."IsEnabled"=CAST(@enabled AS boolean))
             AND (CAST(@afterId AS uuid) IS NULL OR (u."EmployeeNo" COLLATE "C",u."Id")>(CAST(@afterEmployeeNo AS text) COLLATE "C",CAST(@afterId AS uuid)))
             ORDER BY u."EmployeeNo" COLLATE "C",u."Id" LIMIT @take
            """, new { employeeNo = input.EmployeeNo is null ? null : input.EmployeeNo.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%",
                enabled = input.IsEnabled, afterEmployeeNo = input.After?.EmployeeNo, afterId = input.After?.Id, take = input.PageSize + 1 }, token);
        var items = rows.Take(input.PageSize).Select(View).ToArray();
        return new(items, rows.Count > input.PageSize ? new(items[^1].EmployeeNo, items[^1].Id) : null);
    }
    private static UserView View(Row row) => new(row.Id, row.EmployeeNo, row.DisplayName, row.IsEnabled,
        row.MustChangePassword, JsonSerializer.Deserialize<PermissionView[]>(row.PermissionsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!, row.Revision);
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string EmployeeNo { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsEnabled { get; set; }
        public bool MustChangePassword { get; set; }
        public long Revision { get; set; }
        public string PermissionsJson { get; set; } = "[]";
    }
}
