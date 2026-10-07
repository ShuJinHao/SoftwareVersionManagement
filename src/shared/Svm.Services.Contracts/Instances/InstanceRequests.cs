using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Instances;

[RequestPolicy("iam.enrollment.create", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Software, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "enrollment.manage")]
public sealed record CreateEnrollmentGrantCommand(Guid Key, Guid SoftwareId, IReadOnlyList<Guid> DeviceIds, DateTimeOffset ExpiresAt, int MaxInstances, string SecretMaterial, string Reason) : ICommand<GrantView>
{ public override string ToString() => "CreateEnrollmentGrantCommand [redacted]"; }

[RequestPolicy("iam.enrollment.revoke", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Software, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "enrollment.manage")]
public sealed record RevokeEnrollmentGrantCommand(Guid Key, Guid GrantId, long ExpectedRevision, string Reason) : ICommand<GrantView>
{ public override string ToString() => "RevokeEnrollmentGrantCommand [redacted]"; }

[RequestPolicy("iam.recovery.create", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Instance, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "enrollment.manage")]
public sealed record CreateRecoveryGrantCommand(Guid Key, Guid InstanceId, DateTimeOffset ExpiresAt, string SecretMaterial, string Reason) : ICommand<GrantView>
{ public override string ToString() => "CreateRecoveryGrantCommand [redacted]"; }

[RequestPolicy("iam.recovery.revoke", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Instance, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "enrollment.manage")]
public sealed record RevokeRecoveryGrantCommand(Guid Key, Guid GrantId, long ExpectedRevision, string Reason) : ICommand<GrantView>
{ public override string ToString() => "RevokeRecoveryGrantCommand [redacted]"; }

[RequestPolicy("iam.instance-credentials.revoke", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Instance, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "enrollment.manage")]
public sealed record RevokeInstanceCredentialCommand(Guid Key, Guid CredentialId, long ExpectedRevision, string Reason) : ICommand<CredentialView>
{ public override string ToString() => "RevokeInstanceCredentialCommand [redacted]"; }

[RequestPolicy("ins.lifecycle.update", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Instance, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "instance.manage")]
public sealed record UpdateInstanceLifecycleCommand(Guid Key, Guid InstanceId, long ExpectedRevision, string Lifecycle, string Reason) : ICommand<InstanceIdentity>
{ public override string ToString() => "UpdateInstanceLifecycleCommand [redacted]"; }

[RequestPolicy("iam.instances.register", ModuleOwner.Identity, RequestKind.Enrollment, RequestScope.Software, TransactionMode.DatabaseAtomic, IdempotencyMode.EnrollmentProtocol, ValidationMode.Required, ActorKind.EnrollmentGrant, Permission = "instance.register")]
public sealed record RegisterInstanceCommand(Guid Key, Guid SoftwareId, Guid InstallationKey, Guid DeviceId, string SecretMaterial) : ICommand<RegistrationResult>
{ public override string ToString() => "RegisterInstanceCommand [redacted]"; }

[RequestPolicy("iam.instances.recover", ModuleOwner.Identity, RequestKind.Recovery, RequestScope.Instance, TransactionMode.DatabaseAtomic, IdempotencyMode.EnrollmentProtocol, ValidationMode.Required, ActorKind.RecoveryGrant, Permission = "instance.recover")]
public sealed record RecoverInstanceCommand(Guid Key, string SecretMaterial) : ICommand<RegistrationResult>
{ public override string ToString() => "RecoverInstanceCommand [redacted]"; }

[RequestPolicy("ins.report-streams.open", ModuleOwner.Instances, RequestKind.Client, RequestScope.Instance, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Instance, Permission = "instance.report")]
public sealed record OpenReportStreamCommand(Guid Key, long ExpectedEpoch) : ICommand<StreamResult>
{ public override string ToString() => "OpenReportStreamCommand [redacted]"; }

[RequestPolicy("ins.status-reports.accept", ModuleOwner.Instances, RequestKind.Client, RequestScope.Instance, TransactionMode.DatabaseAtomic, IdempotencyMode.ReportSequence, ValidationMode.Required, ActorKind.Instance, Permission = "instance.report")]
public sealed record SubmitStatusReportCommand(StateReport Report) : ICommand<ReportResult>
{ public override string ToString() => "SubmitStatusReportCommand [redacted]"; }

[RequestPolicy("iam.enrollment.list", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Software, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "enrollment.manage")]
public sealed record ListEnrollmentGrantsQuery(Guid SoftwareId, int PageSize = 50, Guid? After = null) : IQuery<InstancePage<GrantView>>;

[RequestPolicy("iam.instance-credentials.list", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Instance, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "enrollment.manage")]
public sealed record ListInstanceCredentialsQuery(Guid InstanceId, int PageSize = 50, Guid? After = null) : IQuery<InstancePage<CredentialView>>;

[RequestPolicy("ins.instances.list", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Software, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record ListInstancesQuery(InstanceListInput Input) : IQuery<InstancePage<InstanceView>>;

[RequestPolicy("ins.instances.get", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Instance, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record GetInstanceQuery(Guid InstanceId) : IQuery<InstanceView>;

[RequestPolicy("ins.instances.history", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Instance, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "instance.read")]
public sealed record GetInstanceHistoryQuery(Guid InstanceId, int PageSize = 50, Guid? After = null) : IQuery<InstancePage<InstallationHistoryView>>;

[RequestPolicy("ins.context.get", ModuleOwner.Instances, RequestKind.Client, RequestScope.Instance, TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Instance, Permission = "instance.report")]
public sealed record GetClientContextQuery() : IQuery<ClientContext>;

public static class InstanceCapabilities
{
    public static bool IsWrite(Type t) => Writes.Contains(t);
    public static bool IsQuery(Type t) => Queries.Contains(t);
    public static bool IsProtocol(Type t) => t == typeof(RegisterInstanceCommand) || t == typeof(RecoverInstanceCommand) || t == typeof(SubmitStatusReportCommand);
    private static readonly HashSet<Type> Writes = [typeof(CreateEnrollmentGrantCommand), typeof(RevokeEnrollmentGrantCommand), typeof(CreateRecoveryGrantCommand), typeof(RevokeRecoveryGrantCommand), typeof(RevokeInstanceCredentialCommand), typeof(UpdateInstanceLifecycleCommand), typeof(RegisterInstanceCommand), typeof(RecoverInstanceCommand), typeof(OpenReportStreamCommand), typeof(SubmitStatusReportCommand)];
    private static readonly HashSet<Type> Queries = [typeof(ListEnrollmentGrantsQuery), typeof(ListInstanceCredentialsQuery), typeof(ListInstancesQuery), typeof(GetInstanceQuery), typeof(GetInstanceHistoryQuery), typeof(GetClientContextQuery)];
}
