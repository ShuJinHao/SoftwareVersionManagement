using MediatR;
using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Packages;

public sealed record PackageCapabilitiesView(long MaxPackageBytes, int HashChunkBytes = 1048576);
public sealed record PackageInput(string FileName, long SizeBytes, string Sha256);
public sealed record ReleaseView(Guid Id, Guid SoftwareId, string Version, string State, string ChangeLevel,
    string ChangeSummary, string ChangeReason, Guid PackageId, bool DownloadAvailable, Guid CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt, string? DisableReason, long Revision,
    Guid? PublishedBy = null, string? PublishedEmployeeNo = null, DateTimeOffset? PublishedAt = null,
    Guid? TestEvidenceId = null, string? PublishReason = null, string? PublishConclusion = null);
public sealed record ReleaseUploadResult(ReleaseView Release, Guid UploadId, string UploadPath);
public sealed record PackageView(Guid Id, Guid ReleaseId, string State, long? SizeBytes, string? Sha256,
    long ExpectedSize, string ExpectedSha256, int HealthyReplicaCount, bool DownloadAvailable,
    string? DownloadPath, string ProcessingStage, string? LastErrorCode, long Revision, Guid UploadId, string FileName);
public sealed record PackageWorkView(Guid Id, string Kind, string State, string Stage, int CompletedItems,
    int TotalItems, string? LastErrorCode, IReadOnlyList<string> BlockingReasons, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, long Revision);
public sealed record ReleasePosition(int Major, int Minor, int Patch, Guid Id);
public sealed record ReleaseListInput(Guid SoftwareId, string? State = null, string? Channel = null,
    int PageSize = 50, ReleasePosition? After = null);
public sealed record ReleasePage<T>(IReadOnlyList<T> Items, ReleasePosition? Next);
public sealed record TestEvidenceView(Guid Id, Guid InstanceId, Guid ReleaseId, string InstalledVersion,
    DateTimeOffset? InstalledAt, DateTimeOffset ReceivedAt, string ReportedRunningState);
public sealed record EvidencePage(IReadOnlyList<TestEvidenceView> Items, Guid? Next);
public sealed record ClientReleaseView(Guid Id, string Version, string State, string ChangeSummary,
    string ChangeReason, Guid PackageId, long SizeBytes, string Sha256, string DownloadPath);
public sealed record DownloadAuditView(Guid RequestId, Guid PackageId, Guid? SubjectId, string ActorKind,
    string? EmployeeNo, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, long? BytesSent, string State);
public sealed record DownloadAuditPage(IReadOnlyList<DownloadAuditView> Items, Guid? Next);

/// <summary>REL owns numbering and state. Cross-module package facts enter only through Application.</summary>
public interface IReleases
{
    Task<IReadOnlyList<ReleaseView>> FormalCandidatesAsync(Guid softwareId, CancellationToken token);
    Task<Guid?> SoftwareForAsync(Guid releaseId, bool protect, CancellationToken token);
    Task<ReleaseView> GetAsync(Guid releaseId, bool protect, CancellationToken token);
    Task<ReleaseView> CreateAsync(CreateReleaseCommand input, Guid packageId, Guid actorId, CancellationToken token);
    Task<ReleaseView> DisableAsync(Guid id, long revision, string reason, CancellationToken token);
    Task<ReleaseView> PublishAsync(PublishReleaseCommand input, Guid actorId, string employeeNo, CancellationToken token);
    Task OpenTestAsync(Guid releaseId, Guid packageId, CancellationToken token);
    Task VerifyInstallationAsync(Guid softwareId, Guid releaseId, string? version, CancellationToken token);
}
public interface IReleaseQueries
{
    Task<ReleasePage<ReleaseView>> ListAsync(ReleaseListInput input, CancellationToken token);
    Task<ReleasePage<ClientReleaseView>> ClientListAsync(ReleaseListInput input, CancellationToken token);
    Task<ReleaseView?> GetAsync(Guid id, CancellationToken token);
    Task<EvidencePage> EvidenceAsync(Guid releaseId, int size, Guid? after, CancellationToken token);
    Task<DownloadAuditPage> DownloadsAsync(Guid softwareId, int size, Guid? after, CancellationToken token);
}
public interface IPackageQueries
{
    Task<PackageView?> GetAsync(Guid id, bool upload, CancellationToken token);
    Task<PackageWorkView?> WorkAsync(Guid id, CancellationToken token);
}

/// <summary>Bounded read projection; current software visibility is checked for every requested software.</summary>
public interface IFormalReleaseAvailability
{
    Task<IReadOnlyDictionary<Guid, Guid>> LatestAsync(IReadOnlyList<Guid> softwareIds, CancellationToken token);
}

// All tokens below are fences for persisted phases, not credentials or caller-granted authority.
public sealed record UploadReceipt(Guid UploadId, Guid PackageId, Guid SoftwareId, Guid ReleaseId,
    Guid ReceiveToken, long ReceiveGeneration, string SourceNode, long ExpectedSize, string ExpectedSha256,
    bool AlreadyReceived);
public sealed record PackageWorkAuthority(Guid WorkId, Guid PackageId, Guid ReleaseId, Guid SoftwareId,
    Guid InitiatorId, string Kind, long DispatchSequence, Guid DispatchEventId, bool Accepted,
    string State, string Stage, string SourceNode, long LeaseGeneration, Guid? LeaseToken,
    DateTimeOffset? LeaseUntil, long ExpectedSize, string ExpectedSha256, string? LeaseNode);
public sealed record PackageLease(PackageWorkAuthority Work, Guid LeaseToken, long LeaseGeneration);
public sealed record ReplicaFact(string NodeId, string State, DateTimeOffset CheckedAt);
public sealed record DownloadAuthorization(Guid RequestId, Guid PackageId, string NodeId,
    long SizeBytes, string Sha256, string InternalUri, bool NewSession);
public sealed record DownloadEnd(Guid RequestId, string NodeId, Guid WorkerGeneration,
    DateTimeOffset EndedAt, long BytesSent, string Outcome);
public interface IPackages
{
    Task<Guid?> SoftwareForAsync(string resource, Guid id, bool protect, CancellationToken token);
    Task<PackageView> GetAsync(Guid id, bool upload, bool protect, CancellationToken token);
    Task<Guid> UploadIdAsync(Guid packageId, CancellationToken token);
    Task<PackageWorkView> WorkAsync(Guid id, CancellationToken token);
    Task<Guid> CreateAsync(Guid id, Guid releaseId, Guid softwareId, Guid initiator, PackageInput input, CancellationToken token);
    Task<UploadReceipt> BeginAsync(Guid uploadId, Guid receiveToken, string nodeId, CancellationToken token);
    Task<UploadReceipt> ReceiptAsync(Guid receiveToken, CancellationToken token);
    Task<UploadReceipt> ReceivingAsync(Guid uploadId, Guid receiveToken, CancellationToken token);
    Task<PackageWorkAuthority> FinishAsync(UploadReceipt receipt, long size, string sha256, CancellationToken token);
    Task FailUploadAsync(Guid uploadId, Guid receiveToken, string code, CancellationToken token);
    Task<PackageWorkAuthority> RetryAsync(Guid id, long revision, Guid initiator, CancellationToken token);
    Task StopAsync(Guid packageId, CancellationToken token);
    Task VerifyPublishReadyAsync(Guid packageId, Guid releaseId, Guid softwareId, CancellationToken token);
    Task<PackageWorkAuthority> AuthorityAsync(Guid workId, bool protect, CancellationToken token);
    Task AcceptAsync(Guid workId, long dispatch, Guid eventId, CancellationToken token);
    Task<bool> HasDispatchAsync(Guid workId, long dispatch, Guid eventId, CancellationToken token);
    Task<IReadOnlyList<Guid>> PendingAsync(DateTimeOffset now, int take, CancellationToken token);
    Task<PackageLease?> ClaimAsync(Guid workId, string nodeId, Guid leaseToken, DateTimeOffset now, CancellationToken token);
    Task RenewAsync(PackageLease lease, DateTimeOffset now, CancellationToken token);
    Task<PackageView> CompleteAsync(PackageLease lease, IReadOnlyList<ReplicaFact> facts, CancellationToken token);
    Task FailWorkAsync(PackageLease lease, string code, CancellationToken token);
    Task<PackageWorkAuthority?> CheckReplicasAsync(Guid packageId, IReadOnlyList<ReplicaFact> facts, CancellationToken token);
    Task<IReadOnlyList<Guid>> ReadyAsync(int take, Guid? after, CancellationToken token);
    Task<DownloadAuthorization> AuthorizeDownloadAsync(Guid packageId, Guid requestId, string nodeId,
        Guid workerGeneration, bool head, Guid subjectId, string actorKind, string? employeeNo, CancellationToken token);
    Task<bool> EndDownloadAsync(DownloadEnd end, CancellationToken token);
    Task<DownloadEnd?> FindDownloadEndAsync(Guid id, string nodeId, Guid workerGeneration, CancellationToken token);
}
public interface IPackageDownloadLogPump { Task PumpAsync(CancellationToken token); }

public sealed record PackageNodeCatalog(IReadOnlyList<string> Nodes)
{
    public void Validate()
    {
        if (Nodes.Count != 2 || Nodes.Distinct(StringComparer.Ordinal).Count() != 2 ||
            Nodes.Any(x => x.Length is < 1 or > 64 || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    public void Require(string node) { Validate(); if (!Nodes.Contains(node, StringComparer.Ordinal)) throw new RequestRejectedException(RequestFailure.PermissionDenied); }
}
public interface IPersonnelWorkAuthorization
{ Task<bool> HasPermissionAsync(Guid subjectId, Guid softwareId, string operation, bool protect, CancellationToken token); }
public interface IPackageDownloadProof
{
    Svm.Services.Contracts.Identity.SessionProof? Person { get; }
    Svm.Services.Contracts.Instances.AccessProof? Instance { get; }
}
/// <summary>Bound by the host to a configured certificate or worker identity, never an HTTP header.</summary>
public interface IPackageServiceIdentity { Guid SubjectId { get; } string NodeId { get; } string Role { get; } }
public static class PackageCapabilities
{
    public static IReadOnlyList<Type> Writes { get; } = Array.AsReadOnly(new[] { typeof(CreateReleaseCommand), typeof(DisableReleaseCommand), typeof(PublishReleaseCommand),
        typeof(RetryPackageCommand), typeof(UploadContentCommand), typeof(BeginUploadCommand), typeof(FinishUploadCommand), typeof(FailUploadCommand),
        typeof(ClaimPackageWorkCommand), typeof(RenewPackageWorkCommand), typeof(CompletePackageWorkCommand), typeof(FailPackageWorkCommand),
        typeof(CheckPackageReplicasCommand), typeof(AuthorizeDownloadCommand), typeof(RecordDownloadEndCommand) });
    public static bool IsWrite(Type type) => Writes.Contains(type);
    public static bool IsIdempotent(Type type) => type == typeof(CreateReleaseCommand) || type == typeof(DisableReleaseCommand) || type == typeof(PublishReleaseCommand) || type == typeof(RetryPackageCommand) || type == typeof(RecordDownloadEndCommand) || IsPhase(type);
    public static bool IsPhase(Type type) => type == typeof(BeginUploadCommand) || type == typeof(FinishUploadCommand) || type == typeof(FailUploadCommand);
    public static bool IsQuery(Type type) => type == typeof(GetUploadTargetQuery) || type == typeof(GetPackageCapabilitiesQuery) || type == typeof(CheckUploadQuery) || type == typeof(ListReleasesQuery) || type == typeof(GetReleaseQuery) ||
        type == typeof(GetPackageQuery) || type == typeof(GetPackageWorkQuery) || type == typeof(GetTestEvidenceQuery) || type == typeof(GetDownloadAuditQuery) ||
        type == typeof(ListClientReleasesQuery) || type == typeof(GetClientReleaseQuery) || type == typeof(GetClientPackageQuery) || type == typeof(GetPackageAuthorityQuery) || type == typeof(InspectReplicaQuery) || type == typeof(GetDownloadEndQuery);
    public static bool IsInternal(Type type) => type == typeof(ClaimPackageWorkCommand) || type == typeof(RenewPackageWorkCommand) ||
        type == typeof(CompletePackageWorkCommand) || type == typeof(FailPackageWorkCommand) || type == typeof(CheckPackageReplicasCommand) ||
        type == typeof(AuthorizeDownloadCommand) || type == typeof(RecordDownloadEndCommand) || type == typeof(GetPackageAuthorityQuery) || type == typeof(InspectReplicaQuery) || type == typeof(GetDownloadEndQuery);
    public static bool IsWorker(Type type) => IsInternal(type) && type != typeof(AuthorizeDownloadCommand) && type != typeof(RecordDownloadEndCommand) && type != typeof(GetDownloadEndQuery);
}

/// <summary>File effects are executed outside any database transaction. IDs alone form paths.</summary>
public interface IPackageFiles
{
    string NodeId { get; }
    Task<IAsyncDisposable> LockUploadAsync(Guid uploadId, CancellationToken token);
    Task<(long Size, string Sha256)> ReceiveAsync(UploadReceipt receipt, Stream content, long? contentLength,
        Func<CancellationToken, Task> verifyCurrent, CancellationToken token);
    Task<IReadOnlyList<ReplicaFact>> PrepareReplicasAsync(PackageLease lease,
        Func<CancellationToken, Task> renew, CancellationToken token);
    Task<IReadOnlyList<ReplicaFact>> InspectAsync(Guid packageId, long size, string sha256, CancellationToken token);
    Task<Stream> OpenReplicaAsync(Guid packageId, CancellationToken token);
    Task ReceiveReplicaAsync(PackageWorkAuthority work, Stream content, Func<CancellationToken, Task> verifyCurrent, CancellationToken token);
    Task<Stream> OpenSourceAsync(PackageWorkAuthority work, CancellationToken token);
    Task<ReplicaFact> InspectLocalAsync(Guid packageId, long size, string sha256, CancellationToken token);
}

/// <summary>Limited phase bridge. Each command uses a new scope and the host's current trusted proof.</summary>
public interface ICommandScopeExecutor
{
    Task<OperationResult<T>> ExecuteAsync<T>(ICommand<T> command, CancellationToken token);
    Task<T> QueryAsync<T>(IQuery<T> query, CancellationToken token);
}
public sealed record PackageExecutionOptions(int LeaseSeconds = 60, int PollSeconds = 5,
    int ReplicaCheckSeconds = 60, int WorkBatchSize = 20)
{
    public void Validate()
    {
        if (LeaseSeconds is < 15 or > 600 || PollSeconds is < 1 or > 60 || ReplicaCheckSeconds is < 5 or > 3600 ||
            WorkBatchSize is < 1 or > 100) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
}
public sealed record PackageLimits(long MaxPackageBytes, int UploadIdleSeconds)
{
    public void Validate()
    {
        if (MaxPackageBytes is < 1 or > 1099511627776 || UploadIdleSeconds is < 5 or > 600)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
}
