using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Packages;

public sealed record OpenDownloadGeneration(string NodeId, Guid WorkerGeneration, long OpenCount, DateTimeOffset LatestStartedAt);
public sealed record PackageCleanupStatus(Guid PackageId, long Revision, string State, Guid? WorkId,
    IReadOnlyList<string> BlockingReasons, IReadOnlyList<OpenDownloadGeneration> OpenDownloads, bool MoreDownloadGenerations);
public sealed record DownloadStopInput(string NodeId, Guid WorkerGeneration, DateTimeOffset StoppedAt,
    bool ProcessStoppedConfirmed, string Evidence, string Reason, long ExpectedRevision);
public sealed record DownloadStopView(Guid Id, Guid PackageId, string NodeId, Guid WorkerGeneration,
    DateTimeOffset StoppedAt, Guid RecordedBy, DateTimeOffset RecordedAt, string Evidence, string Reason);

/// <summary>TSK owns this reference check, including deployments that have not materialized tasks yet.</summary>
public interface IReleaseTaskReferences
{
    Task<bool> HasUnfinishedAsync(Guid releaseId, CancellationToken token);
}

[RequestPolicy("packages.RequestCleanupCommand", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "package.clean")]
public sealed record RequestPackageCleanupCommand(Guid Key, Guid PackageId, long ExpectedRevision, string Reason) : ICommand<PackageWorkView>;
[RequestPolicy("packages.CleanupStatusQuery", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "package.clean")]
public sealed record GetPackageCleanupStatusQuery(Guid PackageId) : IQuery<PackageCleanupStatus>;
[RequestPolicy("packages.RecordDownloadStopCommand", ModuleOwner.Packages, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "package.clean")]
public sealed record RecordDownloadStopCommand(Guid Key, Guid PackageId, DownloadStopInput Input) : ICommand<DownloadStopView>;
[RequestPolicy("packages.RecordCleanupReplicaCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record RecordCleanupReplicaCommand(PackageLease Lease, string NodeId) : ICommand<bool>;
[RequestPolicy("packages.CompleteCleanupCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "package.execute")]
public sealed record CompletePackageCleanupCommand(PackageLease Lease) : ICommand<PackageView>;
