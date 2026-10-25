using Microsoft.EntityFrameworkCore;
using Svm.Core.Identity;
using Svm.SharedKernel.Domain;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Identity;

internal sealed class RoleRepository(SvmDbContext context, IUnitOfWork unit) : IRoleRepository
{
    private void RequireWrite()
    {
        if (unit.CurrentOperationId is null || context.Database.CurrentTransaction is null)
            throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
    }
    public async Task<IReadOnlyList<AssignedRole>> AssignmentsAsync(Guid subjectId, CancellationToken token)
    {
        var rows = await context.Set<RoleAssignment>().AsNoTracking().Where(a => a.SubjectId == subjectId).ToArrayAsync(token);
        var ids = rows.Select(x => x.RoleId).Distinct().ToArray();
        var roles = await context.Set<AccessRole>().AsNoTracking().Where(r => ids.Contains(r.Id)).OrderBy(r => r.Id).ToArrayAsync(token);
        var ops = await context.Set<RoleOperation>().AsNoTracking().Where(o => ids.Contains(o.RoleId)).ToArrayAsync(token);
        return roles.Select(r => new AssignedRole(r.Id.Value, r.Name, r.IsEnabled, r.Revision,
            ops.Where(o => o.RoleId == r.Id).Select(o => o.Operation).Order(StringComparer.Ordinal).ToArray(),
            rows.Where(a => a.RoleId == r.Id).OrderBy(a=>a.ScopeKind).ThenBy(a=>a.ResourceId).Select(a => new RoleScope(a.ScopeKind, a.ResourceId)).ToArray())).ToArray();
    }
    public async Task<RoleDefinition?> GetAsync(Guid roleId, CancellationToken token)
    {
        var role = await context.Set<AccessRole>().FindAsync([new StrongId<AccessRole>(roleId)], token);
        if (role is null) return null;
        await context.Entry(role).ReloadAsync(token);
        return new(role, await context.Set<RoleOperation>().AsNoTracking().Where(o => o.RoleId == new StrongId<AccessRole>(roleId)).Select(o => o.Operation).OrderBy(x => x).ToArrayAsync(token),
            await context.Set<RoleAssignment>().Where(a => a.RoleId == new StrongId<AccessRole>(roleId)).Select(a => a.SubjectId).Distinct().CountAsync(token));
    }
    public async Task<IReadOnlyList<RoleDefinition>> ListAsync(string? name, Guid? afterId, int take, CancellationToken token)
    {
        var roles = await context.Set<AccessRole>().FromSqlInterpolated($"""
            SELECT * FROM iam.roles WHERE ({name}::text IS NULL OR position({name} IN "Name")>0)
              AND ({afterId}::uuid IS NULL OR "Id">{afterId}) ORDER BY "Id" LIMIT {take}
            """).AsNoTracking().ToArrayAsync(token);
        var ids = roles.Select(r => r.Id).ToArray();
        var ops = await context.Set<RoleOperation>().AsNoTracking().Where(o => ids.Contains(o.RoleId)).ToArrayAsync(token);
        var counts = await context.Set<RoleAssignment>().Where(a => ids.Contains(a.RoleId)).GroupBy(a => a.RoleId)
            .Select(g => new { Id = g.Key, Count = g.Select(a => a.SubjectId).Distinct().Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, token);
        return roles.Select(r => new RoleDefinition(r, ops.Where(o => o.RoleId == r.Id).Select(o => o.Operation).Order(StringComparer.Ordinal).ToArray(), counts.GetValueOrDefault(r.Id))).ToArray();
    }
    public Task<bool> NameExistsAsync(string name, Guid? excludedRoleId, CancellationToken token) =>
        context.Set<AccessRole>().AnyAsync(r => r.Name == name && (excludedRoleId == null || r.Id != new StrongId<AccessRole>(excludedRoleId.Value)), token);
    public async Task ProtectSiteChangeAsync(CancellationToken token)
    {
        RequireWrite();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({PersonnelAdministrationRepository.AdministrationLock})", token);
        await context.Set<SubjectGuard>().FromSqlRaw("SELECT * FROM iam.subject_guards ORDER BY \"SubjectId\" FOR UPDATE").AsNoTracking().ToListAsync(token);
    }
    public async Task AdvanceSiteRevisionAsync(CancellationToken token)
    { RequireWrite(); await context.Database.ExecuteSqlRawAsync("UPDATE iam.subject_guards SET \"Revision\"=\"Revision\"+1", token); }
    public async Task ProtectRoleAsync(Guid actorId, Guid roleId, CancellationToken token)
    {
        RequireWrite();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({PersonnelAdministrationRepository.AdministrationLock})", token);
        var ids = await context.Set<RoleAssignment>().Where(a => a.RoleId == new StrongId<AccessRole>(roleId)).Select(a => a.SubjectId).Distinct().ToListAsync(token);
        ids.Add(actorId);
        foreach (var id in ids.Distinct().Order())
            await context.Set<SubjectGuard>().FromSqlInterpolated($"SELECT * FROM iam.subject_guards WHERE \"SubjectId\"={id} FOR UPDATE").AsNoTracking().ToListAsync(token);
    }
    public Task AddAsync(AccessRole role, IReadOnlyList<string> operations, CancellationToken token)
    {
        RequireWrite(); token.ThrowIfCancellationRequested(); context.Add(role);
        foreach (var op in operations) context.Add(new RoleOperation { RoleId = role.Id, Operation = op });
        return Task.CompletedTask;
    }
    public async Task ReplaceOperationsAsync(Guid roleId, IReadOnlyList<string> operations, CancellationToken token)
    {
        RequireWrite(); await context.Set<RoleOperation>().Where(o => o.RoleId == new StrongId<AccessRole>(roleId)).ExecuteDeleteAsync(token);
        foreach (var op in operations) context.Add(new RoleOperation { RoleId = new(roleId), Operation = op });
    }
    public async Task ReplaceAssignmentsAsync(Guid subjectId, IReadOnlyList<(Guid RoleId, RoleScope Scope)> assignments, CancellationToken token)
    {
        RequireWrite(); await context.Set<RoleAssignment>().Where(a => a.SubjectId == subjectId).ExecuteDeleteAsync(token);
        foreach (var a in assignments) context.Add(new RoleAssignment { Id = Guid.NewGuid(), SubjectId = subjectId,
            RoleId = new(a.RoleId), ScopeKind = a.Scope.Kind, ResourceId = a.Scope.ResourceId });
    }
    public async Task<bool> HasEnabledAdministratorAsync(Guid? excludedSubjectId, Guid? excludedRoleId, CancellationToken token) =>
        await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM iam.users u WHERE u."IsEnabled" AND ({excludedSubjectId}::uuid IS NULL OR u."Id"<>{excludedSubjectId})
              AND (EXISTS(SELECT 1 FROM iam.permissions p WHERE p."SubjectId"=u."Id" AND p."SoftwareId" IS NULL AND p."Operation"='identity.manage')
                OR EXISTS(SELECT 1 FROM iam.role_assignments a JOIN iam.roles r ON r."Id"=a."RoleId" JOIN iam.role_operations o ON o."RoleId"=r."Id"
                  WHERE a."SubjectId"=u."Id" AND a."ScopeKind"='Site' AND r."IsEnabled" AND o."Operation"='identity.manage'
                    AND ({excludedRoleId}::uuid IS NULL OR r."Id"<>{excludedRoleId})))) AS "Value"
            """).SingleAsync(token);
    public async Task<bool> HasSoftwarePermissionAsync(Guid subjectId, Guid softwareId, string operation, CancellationToken token) =>
        await context.Database.SqlQuery<bool>($"SELECT iam.has_software({subjectId},{operation},{softwareId}) AS \"Value\"").SingleAsync(token);
    public async Task<bool> HasInstancePermissionAsync(Guid subjectId, Guid softwareId, string operation, Guid instanceId, CancellationToken token) =>
        await context.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM ins.instances i JOIN ins.devices d ON d."Id"=i."DeviceId"
              WHERE i."Id"={instanceId} AND i."SoftwareId"={softwareId}
                AND iam.has_field({subjectId},{operation},d."ProcessId",d."Id",i."SoftwareId")) AS "Value"
            """).SingleAsync(token);
    public async Task<bool> CoversSelectionAsync(Guid subjectId, Guid softwareId, string operation, Guid selectionId, CancellationToken token) =>
        await context.Database.SqlQuery<bool>($"""
            SELECT iam.has_software({subjectId},{operation},{softwareId}) AND
              EXISTS(SELECT 1 FROM tsk.target_selections s WHERE s."Id"={selectionId} AND s."SoftwareId"={softwareId}) AND
              NOT EXISTS(SELECT 1 FROM tsk.target_members m JOIN ins.instances i ON i."Id"=m."InstanceId" JOIN ins.devices d ON d."Id"=i."DeviceId"
                WHERE m."SelectionId"={selectionId} AND i."SoftwareId"={softwareId}
                  AND NOT iam.has_field({subjectId},{operation},d."ProcessId",d."Id",i."SoftwareId")) AS "Value"
            """).SingleAsync(token);
    public Task<long> RevisionAsync(Guid id, CancellationToken token) => context.Set<SubjectGuard>().Where(g => g.SubjectId == id).Select(g => g.Revision).SingleOrDefaultAsync(token);
    public async Task AdvanceAssignedRevisionsAsync(Guid roleId, CancellationToken token)
    {
        RequireWrite(); await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE iam.subject_guards SET "Revision"="Revision"+1
            WHERE "SubjectId" IN (SELECT "SubjectId" FROM iam.role_assignments WHERE "RoleId"={roleId})
            """, token);
    }
}
