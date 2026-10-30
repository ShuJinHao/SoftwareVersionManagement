using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
namespace Svm.Services.Contracts.Plc;

[RequestPolicy("plc.CreatePlcCredentialCommand", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "plc.uploadCredential.manage")]
public sealed record CreatePlcCredentialCommand(Guid Key, Guid SoftwareId, string Name, IReadOnlyList<PlcScope> Scope, DateTimeOffset ExpiresAt, string SecretMaterial) : ICommand<PlcCredentialView>
{ public override string ToString() => "CreatePlcCredentialCommand [redacted]"; }

[RequestPolicy("plc.RevokePlcCredentialCommand", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "plc.uploadCredential.manage")]
public sealed record RevokePlcCredentialCommand(Guid Key, Guid CredentialId, long ExpectedRevision, string Reason) : ICommand<PlcCredentialView>;

[RequestPolicy("plc.PublishPlcReleaseCommand", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.publish")]
public sealed record PublishPlcReleaseCommand(Guid Key, Guid ReleaseId, long ExpectedRevision, Guid BindingId, long BindingRevision, DateTimeOffset FieldAt, string Verification, string Materials, string PublishReason, string PublishConclusion) : ICommand<ReleaseView>;

[RequestPolicy("plc.RecordPlcUsageCommand", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "plc.usage.record")]
public sealed record RecordPlcUsageCommand(Guid Key, Guid BindingId, long BindingRevision, Guid ReleaseId, long ExpectedRevision, DateTimeOffset FieldAt, string Note, string Materials, Guid? CorrectsId = null) : ICommand<PlcUsageView>;

[RequestPolicy("plc.ListPlcCredentialsQuery", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "plc.uploadCredential.manage")]
public sealed record ListPlcCredentialsQuery(Guid SoftwareId, int PageSize = 50, Guid? After = null) : IQuery<PlcPage<PlcCredentialView>>;

[RequestPolicy("plc.ListPlcUsageQuery", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record ListPlcUsageQuery(Guid HardwareId, int PageSize = 50, Guid? After = null) : IQuery<PlcPage<PlcUsageView>>;

[RequestPolicy("plc.GetPlcUsageQuery", ModuleOwner.Instances, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetPlcUsageQuery(Guid BindingId) : IQuery<PlcUsageSummary>;

[RequestPolicy("plc.GetPlcEvidenceQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetPlcEvidenceQuery(Guid ReleaseId) : IQuery<PlcEvidenceView?>;

public static class PlcCapabilities
{
    public static bool IsHumanWrite(Type t) => t == typeof(CreatePlcCredentialCommand) || t == typeof(RevokePlcCredentialCommand) || t == typeof(PublishPlcReleaseCommand) || t == typeof(RecordPlcUsageCommand);
    public static bool IsHumanQuery(Type t) => t == typeof(ListPlcCredentialsQuery) || t == typeof(ListPlcUsageQuery) || t == typeof(GetPlcUsageQuery) || t == typeof(GetPlcEvidenceQuery);
    public static bool IsUpload(Type t) => t == typeof(CreatePlcArchiveCommand) || t == typeof(PlcUploadContentCommand) || t == typeof(PlcBeginUploadCommand) || t == typeof(PlcFinishUploadCommand) || t == typeof(PlcFailUploadCommand) || t == typeof(PlcCheckUploadQuery) || t == typeof(PlcRetryPackageCommand) || t == typeof(PlcGetPackageQuery) || t == typeof(PlcGetPackageWorkQuery) || t == typeof(PlcGetUploadTargetQuery);
    public static bool IsPhase(Type t) => t == typeof(PlcBeginUploadCommand) || t == typeof(PlcFinishUploadCommand) || t == typeof(PlcFailUploadCommand);
    public static bool IsIdempotent(Type t) => IsHumanWrite(t) || IsPhase(t) || t == typeof(CreatePlcArchiveCommand) || t == typeof(PlcRetryPackageCommand);
    public static bool IsWrite(Type t) => IsIdempotent(t) || t == typeof(PlcUploadContentCommand);
    public static bool Contains(Type t) => IsHumanWrite(t) || IsHumanQuery(t) || IsUpload(t);
}
