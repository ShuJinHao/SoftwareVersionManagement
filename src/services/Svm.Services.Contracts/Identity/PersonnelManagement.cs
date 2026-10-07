using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Identity;

public sealed record UserView(Guid Id, string EmployeeNo, string DisplayName, bool IsEnabled,
    bool MustChangePassword, IReadOnlyList<PermissionView> Permissions, long Revision);
public sealed record UserPagePosition(string EmployeeNo, Guid Id);
public sealed record UserListInput(string? EmployeeNo, bool? IsEnabled, int PageSize, UserPagePosition? After);
public sealed record UserPage(IReadOnlyList<UserView> Items, UserPagePosition? Next);
public sealed record PersonnelManagementOptions(int DefaultPageSize = 50, int MaximumPageSize = 200, int CursorMinutes = 15)
{
    public void Validate()
    {
        if (DefaultPageSize < 1 || MaximumPageSize < DefaultPageSize || MaximumPageSize > 200 || CursorMinutes is < 1 or > 60)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
}

public interface IUserQueries
{
    Task<UserView?> GetAsync(Guid userId, CancellationToken cancellationToken);
    Task<UserPage> ListAsync(UserListInput input, CancellationToken cancellationToken);
}
public interface IPersonnelAdministration
{
    Task ProtectAsync(Guid actorId, Guid? targetId, CancellationToken cancellationToken);
    Task<UserView> CreateAsync(string employeeNo, string displayName, string temporaryPassword, CancellationToken cancellationToken);
    Task<UserView> UpdateAsync(Guid userId, long expectedRevision, string? displayName, bool? isEnabled, CancellationToken cancellationToken);
    Task<UserView> ResetPasswordAsync(Guid userId, long expectedRevision, string temporaryPassword, CancellationToken cancellationToken);
    Task<UserView> ReplacePermissionsAsync(Guid userId, long expectedRevision, IReadOnlyList<PermissionView> permissions, CancellationToken cancellationToken);
}

[RequestPolicy("identity.users.list", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record ListUsersQuery(UserListInput Input) : IQuery<UserPage>;
[RequestPolicy("identity.users.get", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record GetUserQuery(Guid UserId) : IQuery<UserView>;

[RequestPolicy("identity.users.create", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed class CreateUserCommand(Guid key, string employeeNo, string displayName, string temporaryPassword) : ICommand<UserView>
{
    public Guid Key { get; } = key;
    public string EmployeeNo { get; } = employeeNo;
    public string DisplayName { get; } = displayName;
    public string TemporaryPassword { get; } = temporaryPassword;
    public override string ToString() => "CreateUserCommand [redacted]";
}
[RequestPolicy("identity.users.update", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed record UpdateUserCommand(Guid Key, Guid UserId, long ExpectedRevision, string? DisplayName, bool? IsEnabled, string Reason) : ICommand<UserView>;
[RequestPolicy("identity.users.reset-password", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed class ResetUserPasswordCommand(Guid key, Guid userId, long expectedRevision, string temporaryPassword, string reason) : ICommand<UserView>
{
    public Guid Key { get; } = key;
    public Guid UserId { get; } = userId;
    public long ExpectedRevision { get; } = expectedRevision;
    public string TemporaryPassword { get; } = temporaryPassword;
    public string Reason { get; } = reason;
    public override string ToString() => "ResetUserPasswordCommand [redacted]";
}
[RequestPolicy("identity.users.permissions", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "identity.manage")]
public sealed class ReplaceUserPermissionsCommand(Guid key, Guid userId, long expectedRevision, IReadOnlyList<PermissionView> permissions, string reason) : ICommand<UserView>
{
    public Guid Key { get; } = key;
    public Guid UserId { get; } = userId;
    public long ExpectedRevision { get; } = expectedRevision;
    public IReadOnlyList<PermissionView> Permissions { get; } = Array.AsReadOnly(permissions.ToArray());
    public string Reason { get; } = reason;
}
/// <summary>Type identity, never copied metadata or a request-supplied operation, activates these writes.</summary>
public static class PersonnelManagementCapabilities
{
    public static bool Contains(Type type) => type == typeof(CreateUserCommand) || type == typeof(UpdateUserCommand) ||
        type == typeof(ResetUserPasswordCommand) || type == typeof(ReplaceUserPermissionsCommand);
    public static Guid? Target(object request) => request switch
    {
        UpdateUserCommand x => x.UserId, ResetUserPasswordCommand x => x.UserId,
        ReplaceUserPermissionsCommand x => x.UserId, CreateUserCommand => null,
        _ => throw new RequestRejectedException(RequestFailure.ConfigurationInvalid)
    };
    public static IReadOnlyList<string> GlobalPermissions { get; } = Array.AsReadOnly(new[]
        { "identity.manage", "software.create", "asset.read", "asset.manage" });
}
