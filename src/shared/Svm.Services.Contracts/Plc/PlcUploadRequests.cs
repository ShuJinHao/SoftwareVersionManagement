using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
namespace Svm.Services.Contracts.Plc;

[RequestPolicy("plc.PlcUploadContentCommand", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.PhasedFile, IdempotencyMode.None, ValidationMode.Required, ActorKind.PlcUpload, Permission = "release.upload")]
public sealed record PlcUploadContentCommand(Guid UploadId, Stream Content, long? ContentLength) : ICommand<PackageView>;

[RequestPolicy("plc.PlcBeginUploadCommand", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.PlcUpload, Permission = "release.upload")]
public sealed record PlcBeginUploadCommand(Guid UploadId, Guid ReceiveToken, string NodeId) : ICommand<UploadReceipt>;

[RequestPolicy("plc.PlcFinishUploadCommand", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.PlcUpload, Permission = "release.upload")]
public sealed record PlcFinishUploadCommand(UploadReceipt Receipt, long Size, string Sha256) : ICommand<PackageView>;

[RequestPolicy("plc.PlcFailUploadCommand", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.PlcUpload, Permission = "release.upload")]
public sealed record PlcFailUploadCommand(Guid UploadId, Guid ReceiveToken, string Code) : ICommand<PackageView>;

[RequestPolicy("plc.PlcCheckUploadQuery", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.PlcUpload, Permission = "release.upload")]
public sealed record PlcCheckUploadQuery(Guid UploadId, Guid ReceiveToken) : IQuery<UploadReceipt>;

[RequestPolicy("plc.PlcRetryPackageCommand", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.PlcUpload, Permission = "release.upload")]
public sealed record PlcRetryPackageCommand(Guid Key, Guid PackageId, long ExpectedRevision, string Reason) : ICommand<PackageView>;

[RequestPolicy("plc.PlcGetPackageQuery", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.PlcUpload, Permission = "software.read")]
public sealed record PlcGetPackageQuery(Guid PackageId, bool Upload = false) : IQuery<PackageView>;

[RequestPolicy("plc.PlcGetPackageWorkQuery", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.PlcUpload, Permission = "software.read")]
public sealed record PlcGetPackageWorkQuery(Guid WorkId) : IQuery<PackageWorkView>;

[RequestPolicy("plc.upload.target", ModuleOwner.Packages, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.PlcUpload, Permission = "release.upload")]
public sealed record PlcGetUploadTargetQuery(Guid UploadId) : IQuery<string>;

[RequestPolicy("plc.CreatePlcArchiveCommand", ModuleOwner.Releases, RequestKind.PlcUpload, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.PlcUpload, Permission = "release.upload")]
public sealed record CreatePlcArchiveCommand(Guid Key, Guid BindingId, long BindingRevision, string ChangeLevel, string ChangeSummary,
    string ChangeReason, string? ExpectedVersion, PackageInput Package) : ICommand<ReleaseUploadResult>;
