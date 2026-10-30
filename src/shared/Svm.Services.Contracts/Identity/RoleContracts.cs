using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Identity;

public sealed record AccessScope(string Kind, Guid? ResourceId, string? Name = null);
public sealed record RoleView(Guid Id, string Name, bool IsEnabled, IReadOnlyList<string> Operations, int AssignedPeople, long Revision);
public sealed record RoleAssignmentView(Guid RoleId, string Name, bool IsEnabled, long Revision,
    IReadOnlyList<string> Operations, IReadOnlyList<AccessScope> Scopes);
public sealed record RoleAssignmentInput(Guid RoleId, IReadOnlyList<AccessScope> Scopes);
public sealed record RolePage(IReadOnlyList<RoleView> Items, Guid? Next);
public sealed record AuthorizationView(Guid SubjectId, long Revision, IReadOnlyList<PermissionView> DirectPermissions,
    IReadOnlyList<RoleAssignmentView> Roles)
{
    public IReadOnlyDictionary<Guid, string> SoftwareNames { get; init; } = new Dictionary<Guid, string>();
    public static AuthorizationView Legacy(PersonnelView person) => new(person.SubjectId, 0, person.Permissions, []);
    public bool Global(string operation) => DirectPermissions.Any(p => p.SoftwareId is null && p.Operation == operation) ||
        Roles.Any(r => r.IsEnabled && r.Operations.Contains(operation) && r.Scopes.Any(s => s.Kind == "Site"));
    public bool AnyField(string operation) =>
        DirectPermissions.Any(p => p.Operation == operation || operation == "asset.create" && p.SoftwareId is null && p.Operation == "asset.manage") ||
        Roles.Any(r => r.IsEnabled && r.Operations.Contains(operation) && r.Scopes.Any(s => s.Kind is "Site" or "Process" or "Device"));
    public bool Field(string operation, Guid? processId, Guid? deviceId, Guid? softwareId = null)
    {
        // Legacy direct grants keep their original meaning. New role grants never borrow another role's scope.
        if (operation == "asset.create" && DirectPermissions.Any(p => p.SoftwareId is null && p.Operation == "asset.manage")) return true;
        if (operation == "binding.manage" && DirectPermissions.Any(p => p.SoftwareId is null && p.Operation == "asset.manage") &&
            softwareId is { } sid && DirectPermissions.Any(p => p.SoftwareId == sid && p.Operation == "instance.manage")) return true;
        if (DirectPermissions.Any(p => p.Operation == operation && (p.SoftwareId is null || p.SoftwareId == softwareId))) return true;
        return RoleOperationCatalog.Field.Contains(operation) && Roles.Any(r => r.IsEnabled && r.Operations.Contains(operation) && r.Scopes.Any(s =>
            s.Kind == "Site" || s.Kind == "Process" && s.ResourceId == processId || s.Kind == "Device" && s.ResourceId == deviceId));
    }
    public bool Product(string operation, Guid softwareId) =>
        DirectPermissions.Any(p => p.SoftwareId == softwareId && p.Operation == operation) ||
        RoleOperationCatalog.Product.Contains(operation) && Roles.Any(r => r.IsEnabled && r.Operations.Contains(operation) && r.Scopes.Any(s => s.Kind == "Software" && s.ResourceId == softwareId));
    public IReadOnlyList<AccessScope> FieldScopes(string operation) => Roles.Where(r => r.IsEnabled && r.Operations.Contains(operation))
        .SelectMany(r => r.Scopes).Where(s => s.Kind is "Site" or "Process" or "Device").Distinct().ToArray();
}

public static class RoleOperationCatalog
{
    public static IReadOnlyList<string> Platform { get; } = ["identity.manage"];
    public static IReadOnlyList<string> Field { get; } = ["asset.read", "asset.create", "asset.manage", "software.create", "binding.manage",
        "software.read", "instance.read", "instance.manage", "enrollment.manage", "deployment.create", "deployment.control", "task.closeUnknown", "plc.verify", "plc.usage.record", "audit.read"];
    public static IReadOnlyList<string> Product { get; } = ["software.read", "release.source.manage", "plc.uploadCredential.manage", "release.upload", "release.publish", "release.disable", "audit.read"];
    public static IReadOnlyList<string> All { get; } = Platform.Concat(Field).Concat(Product).Distinct().Order(StringComparer.Ordinal).ToArray();
    public static bool IsOpen(string operation) => All.Contains(operation);
}

public interface IRoleQueries
{
    Task<AuthorizationView> GetAsync(Guid subjectId, CancellationToken token);
    Task<RoleView?> RoleAsync(Guid roleId, CancellationToken token);
    Task<RolePage> ListAsync(string? name, Guid? afterId, int pageSize, CancellationToken token);
}
public interface IRoleAuthorization : IRoleQueries
{
    Task ProtectSiteChangeAsync(CancellationToken token);
    Task AdvanceSiteRevisionAsync(CancellationToken token);
    Task ProtectRoleAsync(Guid actorId, Guid roleId, CancellationToken token);
    Task<RoleView> CreateAsync(string name, IReadOnlyList<string> operations, CancellationToken token);
    Task<RoleView> UpdateAsync(Guid roleId, long revision, string name, bool enabled, IReadOnlyList<string> operations, CancellationToken token);
    Task<AuthorizationView> ReplaceAsync(Guid subjectId, long revision, IReadOnlyList<RoleAssignmentInput> assignments,
        IReadOnlyList<PermissionView> directPermissions, CancellationToken token);
}

[RequestPolicy("identity.roles.list", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record ListRolesQuery(string? Name, Guid? AfterId, int PageSize) : IQuery<RolePage>;
[RequestPolicy("identity.roles.get", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record GetRoleQuery(Guid RoleId) : IQuery<RoleView>;
[RequestPolicy("identity.authorization.get", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record GetAuthorizationQuery(Guid SubjectId) : IQuery<AuthorizationView>;
[RequestPolicy("identity.authorization.self", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "session.self")]
public sealed record GetMyAuthorizationQuery : IQuery<AuthorizationView>;
[RequestPolicy("identity.roles.create", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record CreateRoleCommand(Guid Key, string Name, IReadOnlyList<string> Operations, string Reason) : ICommand<RoleView>;
[RequestPolicy("identity.roles.update", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record UpdateRoleCommand(Guid Key, Guid RoleId, long ExpectedRevision, string Name, bool IsEnabled, IReadOnlyList<string> Operations, string Reason) : ICommand<RoleView>;
[RequestPolicy("identity.authorization.replace", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record ReplaceAuthorizationCommand(Guid Key, Guid SubjectId, long ExpectedRevision, IReadOnlyList<RoleAssignmentInput> Assignments,
    IReadOnlyList<PermissionView> DirectPermissions, string Reason) : ICommand<AuthorizationView>;

public static class RoleCapabilities
{
    public static bool IsWrite(Type t) => t == typeof(CreateRoleCommand) || t == typeof(UpdateRoleCommand) || t == typeof(ReplaceAuthorizationCommand);
    public static bool IsQuery(Type t) => t == typeof(ListRolesQuery) || t == typeof(GetRoleQuery) || t == typeof(GetAuthorizationQuery) || t == typeof(GetMyAuthorizationQuery);
}
