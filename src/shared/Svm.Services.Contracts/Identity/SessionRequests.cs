using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Identity;

[RequestPolicy("session.anonymous", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Anonymous,
    ValidationReason = "No input.")]
public sealed class AnonymousSessionQuery : IQuery<PersonnelView?>;

[RequestPolicy("session.current", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human,
    Permission = "session.self", ValidationReason = "Identity comes from the protected session.")]
public sealed class CurrentSessionQuery : IQuery<PersonnelView?>;

[RequestPolicy("session.login", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Anonymous)]
public sealed class LoginCommand(string employeeNo, string password) : ICommand<PersonnelMutation>
{
    public string EmployeeNo { get; } = employeeNo;
    public string Password { get; } = password;
    public override string ToString() => "LoginCommand [redacted]";
}

[RequestPolicy("session.logout", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human,
    Permission = "session.self", ValidationReason = "Current protected session only.")]
public sealed class LogoutCommand : ICommand<PersonnelMutation>;

[RequestPolicy("session.password", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "session.self")]
public sealed class ChangePasswordCommand(string currentPassword, string newPassword) : ICommand<PersonnelMutation>
{
    public string CurrentPassword { get; } = currentPassword;
    public string NewPassword { get; } = newPassword;
    public override string ToString() => "ChangePasswordCommand [redacted]";
}

[RequestPolicy("identity.seed", ModuleOwner.Identity, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "identity.initialize")]
public sealed class SeedPersonnelCommand(string employeeNo, string displayName, string temporaryPassword) : ICommand<PersonnelMutation>
{
    public string EmployeeNo { get; } = employeeNo;
    public string DisplayName { get; } = displayName;
    public string TemporaryPassword { get; } = temporaryPassword;
    public override string ToString() => "SeedPersonnelCommand [redacted]";
}

/// <summary>Closed activation list. Operation names alone never enable a write request.</summary>
public static class PersonnelWriteCapabilities
{
    public static bool Contains(Type type) => type == typeof(LoginCommand) || type == typeof(LogoutCommand) ||
        type == typeof(ChangePasswordCommand) || type == typeof(SeedPersonnelCommand);
}
