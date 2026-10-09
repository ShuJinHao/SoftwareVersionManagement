using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Packages;

[RequestPolicy("releases.CreateReleaseCommand", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record CreateReleaseCommand(Guid Key, Guid SoftwareId, string ChangeLevel, string ChangeSummary, string ChangeReason, string? ExpectedVersion, PackageInput Package) : ICommand<ReleaseUploadResult>;

[RequestPolicy("releases.DisableReleaseCommand", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.disable")]
public sealed record DisableReleaseCommand(Guid Key, Guid ReleaseId, long ExpectedRevision, string Reason) : ICommand<ReleaseView>;

[RequestPolicy("releases.PublishReleaseCommand", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.publish")]
public sealed record PublishReleaseCommand(Guid Key, Guid ReleaseId, long ExpectedRevision, Guid TestEvidenceId,
    string PublishReason, string PublishConclusion) : ICommand<ReleaseView>;

[RequestPolicy("packages.RetryPackageCommand", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record RetryPackageCommand(Guid Key, Guid PackageId, long ExpectedRevision, string Reason) : ICommand<PackageView>;

[RequestPolicy("packages.UploadContentCommand", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.PhasedFile, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record UploadContentCommand(Guid UploadId, Stream Content, long? ContentLength) : ICommand<PackageView>;

[RequestPolicy("packages.BeginUploadCommand", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record BeginUploadCommand(Guid UploadId, Guid ReceiveToken, string NodeId) : ICommand<UploadReceipt>;

[RequestPolicy("packages.FinishUploadCommand", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record FinishUploadCommand(UploadReceipt Receipt, long Size, string Sha256) : ICommand<PackageView>;

[RequestPolicy("packages.FailUploadCommand", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record FailUploadCommand(Guid UploadId, Guid ReceiveToken, string Code) : ICommand<PackageView>;

[RequestPolicy("packages.CheckUploadQuery", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record CheckUploadQuery(Guid UploadId, Guid ReceiveToken) : IQuery<UploadReceipt>;

[RequestPolicy("releases.ListReleasesQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record ListReleasesQuery(ReleaseListInput Input) : IQuery<ReleasePage<ReleaseView>>;

[RequestPolicy("releases.GetReleaseQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetReleaseQuery(Guid ReleaseId) : IQuery<ReleaseView>;

[RequestPolicy("packages.GetPackageQuery", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetPackageQuery(Guid PackageId, bool Upload = false) : IQuery<PackageView>;

[RequestPolicy("packages.GetPackageWorkQuery", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetPackageWorkQuery(Guid WorkId) : IQuery<PackageWorkView>;

[RequestPolicy("releases.GetTestEvidenceQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetTestEvidenceQuery(Guid ReleaseId, int PageSize = 50, Guid? After = null) : IQuery<EvidencePage>;

[RequestPolicy("packages.GetDownloadAuditQuery", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "audit.read")]
public sealed record GetDownloadAuditQuery(Guid SoftwareId, int PageSize = 50, Guid? After = null) : IQuery<DownloadAuditPage>;

[RequestPolicy("releases.ListClientReleasesQuery", ModuleOwner.Releases, RequestKind.Client, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Instance, Permission = "software.read")]
public sealed record ListClientReleasesQuery(ReleaseListInput Input) : IQuery<ReleasePage<ClientReleaseView>>;

[RequestPolicy("releases.GetClientReleaseQuery", ModuleOwner.Releases, RequestKind.Client, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Instance, Permission = "software.read")]
public sealed record GetClientReleaseQuery(Guid ReleaseId) : IQuery<ClientReleaseView>;

[RequestPolicy("packages.GetClientPackageQuery", ModuleOwner.Packages, RequestKind.Client, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Instance, Permission = "software.read")]
public sealed record GetClientPackageQuery(Guid PackageId) : IQuery<PackageView>;

[RequestPolicy("packages.ClaimPackageWorkCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record ClaimPackageWorkCommand(Guid WorkId, string NodeId, Guid LeaseToken) : ICommand<PackageLease?>;

[RequestPolicy("packages.RenewPackageWorkCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record RenewPackageWorkCommand(PackageLease Lease) : ICommand<bool>;

[RequestPolicy("packages.CompletePackageWorkCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record CompletePackageWorkCommand(PackageLease Lease, IReadOnlyList<ReplicaFact> Facts) : ICommand<PackageView>;

[RequestPolicy("packages.FailPackageWorkCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record FailPackageWorkCommand(PackageLease Lease, string Code) : ICommand<bool>;

[RequestPolicy("packages.CheckPackageReplicasCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record CheckPackageReplicasCommand(Guid PackageId, IReadOnlyList<ReplicaFact> Facts) : ICommand<bool>;

[RequestPolicy("packages.GetPackageAuthorityQuery", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record GetPackageAuthorityQuery(Guid WorkId) : IQuery<PackageWorkAuthority>;

[RequestPolicy("packages.AuthorizeDownloadCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record AuthorizeDownloadCommand(Guid PackageId, Guid RequestId, string NodeId, Guid WorkerGeneration, bool Head) : ICommand<DownloadAuthorization>;

[RequestPolicy("packages.RecordDownloadEndCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record RecordDownloadEndCommand(DownloadEnd End) : ICommand<bool>;

[RequestPolicy("pkg.replica.inspect", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record InspectReplicaQuery(Guid PackageId) : IQuery<PackageView>;

[RequestPolicy("pkg.capabilities.get", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Global,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetPackageCapabilitiesQuery : IQuery<PackageCapabilitiesView>;

[RequestPolicy("pkg.upload.target", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record GetUploadTargetQuery(Guid UploadId) : IQuery<string>;

[RequestPolicy("pkg.download.end.get", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record GetDownloadEndQuery(Guid RequestId, string NodeId, Guid WorkerGeneration) : IQuery<DownloadEnd?>;
