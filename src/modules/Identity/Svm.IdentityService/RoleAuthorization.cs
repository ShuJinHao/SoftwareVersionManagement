using Svm.Core.Identity;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.IdentityService;

internal sealed class RoleAuthorization(IRoleRepository roles, IPersonnelRepository personnel, IPersonnelAdministrationRepository administration,
    IUnitOfWork unit) : IRoleAuthorization
{
    public async Task<AuthorizationView> GetAsync(Guid subjectId, CancellationToken token)
    {
        var revision = await roles.RevisionAsync(subjectId, token);
        if (revision == 0) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        return new(subjectId, revision,
            (await personnel.PermissionsAsync(subjectId, token)).Select(p => new PermissionView(p.SoftwareId, p.Operation)).ToArray(),
            (await roles.AssignmentsAsync(subjectId, token)).Select(r => new RoleAssignmentView(r.RoleId, r.Name, r.IsEnabled, r.Revision,
                r.Operations, r.Scopes.Select(s => new AccessScope(s.Kind, s.ResourceId)).ToArray())).ToArray());
    }
    public async Task<RoleView?> RoleAsync(Guid id, CancellationToken token) => await roles.GetAsync(id, token) is { } r ? View(r) : null;
    public async Task<RolePage> ListAsync(string? name, Guid? afterId, int pageSize, CancellationToken token)
    {
        var rows = await roles.ListAsync(name, afterId, pageSize + 1, token); var items = rows.Take(pageSize).Select(View).ToArray();
        return new(items, rows.Count > pageSize ? items[^1].Id : null);
    }
    public Task ProtectSiteChangeAsync(CancellationToken token) => roles.ProtectSiteChangeAsync(token);
    public Task AdvanceSiteRevisionAsync(CancellationToken token) => roles.AdvanceSiteRevisionAsync(token);
    public Task ProtectRoleAsync(Guid actorId, Guid roleId, CancellationToken token) => roles.ProtectRoleAsync(actorId, roleId, token);
    public async Task<RoleView> CreateAsync(string name, IReadOnlyList<string> operations, CancellationToken token)
    {
        RequireWrite();
        if (await roles.NameExistsAsync(name, null, token)) throw new RequestRejectedException(RequestFailure.InvalidState);
        var role = new AccessRole(new(Guid.NewGuid()), name);
        await roles.AddAsync(role, operations, token); return new(role.Id.Value, role.Name, role.IsEnabled, operations, 0, role.Revision);
    }
    public async Task<RoleView> UpdateAsync(Guid id, long revision, string name, bool enabled, IReadOnlyList<string> operations, CancellationToken token)
    {
        RequireWrite(); var current = await roles.GetAsync(id, token) ?? throw Missing();
        if (current.Role.Revision != revision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (await roles.NameExistsAsync(name, id, token)) throw new RequestRejectedException(RequestFailure.InvalidState);
        if (current.Role.IsEnabled && current.Operations.Contains("identity.manage") && (!enabled || !operations.Contains("identity.manage")) &&
            !await roles.HasEnabledAdministratorAsync(null, id, token)) throw new RequestRejectedException(RequestFailure.InvalidState);
        current.Role.Update(name, enabled); await roles.ReplaceOperationsAsync(id, operations, token);
        await roles.AdvanceAssignedRevisionsAsync(id, token);
        return new(id, name, enabled, operations, current.AssignedPeople, current.Role.Revision);
    }
    public async Task<AuthorizationView> ReplaceAsync(Guid subjectId, long revision, IReadOnlyList<RoleAssignmentInput> assignments,
        IReadOnlyList<PermissionView> directPermissions, CancellationToken token)
    {
        RequireWrite(); var user = await personnel.GetAsync(subjectId, token) ?? throw Missing();
        var current = await GetAsync(subjectId, token);
        if (current.Revision != revision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (directPermissions.Except(current.DirectPermissions).Any(p => !RoleOperationCatalog.IsOpen(p.Operation) ||
            !PermissionCatalog.Entries.Contains((p.Operation, p.SoftwareId is null)))) throw new RequestRejectedException(RequestFailure.ValidationFailed);
        var assigned = new List<RoleAssignmentView>();
        foreach (var a in assignments.OrderBy(a => a.RoleId))
        {
            var r = await roles.GetAsync(a.RoleId, token) ?? throw Missing();
            if (!r.Role.IsEnabled && !current.Roles.Any(x => x.RoleId == a.RoleId)) throw new RequestRejectedException(RequestFailure.InvalidState);
            assigned.Add(new(a.RoleId, r.Role.Name, r.Role.IsEnabled, r.Role.Revision, r.Operations, a.Scopes));
        }
        var proposed = new AuthorizationView(subjectId, revision + 1, directPermissions, assigned);
        if (user.IsEnabled && current.Global("identity.manage") && !proposed.Global("identity.manage") &&
            !await roles.HasEnabledAdministratorAsync(subjectId, null, token)) throw new RequestRejectedException(RequestFailure.InvalidState);
        await roles.ReplaceAssignmentsAsync(subjectId, assignments.SelectMany(a => a.Scopes.Select(s => (a.RoleId, new RoleScope(s.Kind, s.ResourceId)))).ToArray(), token);
        await administration.ReplaceAllPermissionsAsync(subjectId, directPermissions.Select(p => new PersonnelPermission(p.SoftwareId, p.Operation)).ToArray(), token);
        await personnel.AdvanceRevisionAsync(subjectId, token);
        return proposed;
    }
    private static RoleView View(RoleDefinition r) => new(r.Role.Id.Value, r.Role.Name, r.Role.IsEnabled, r.Operations, r.AssignedPeople, r.Role.Revision);
    private static RequestRejectedException Missing() => new(RequestFailure.ResourceNotFound);
    private void RequireWrite() { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
