using Svm.SharedKernel.Domain;

namespace Svm.Core.Packages;

public sealed class PackageAsset : AggregateRoot<StrongId<PackageAsset>>
{
    private PackageAsset(StrongId<PackageAsset> id) : base(id) { }
    public PackageAsset(Guid id, Guid releaseId, Guid softwareId, string fileName, long expectedSize, string expectedSha256) : base(new(id))
    { ReleaseId = releaseId; SoftwareId = softwareId; FileName = fileName; ExpectedSize = expectedSize; ExpectedSha256 = expectedSha256; }
    public Guid UploadId { get; private set; } = Guid.NewGuid();
    public Guid ReleaseId { get; private set; }
    public Guid SoftwareId { get; private set; }
    public string FileName { get; private set; } = "";
    public long ExpectedSize { get; private set; }
    public string ExpectedSha256 { get; private set; } = "";
    public long? SizeBytes { get; private set; }
    public string? Sha256 { get; private set; }
    public string State { get; private set; } = "Uploading";
    public bool Disabled { get; private set; }
    public long Revision { get; private set; } = 1;
    public void Verify(long size, string sha256)
    {
        if (Disabled || size != ExpectedSize || sha256 != ExpectedSha256 || State == "Ready")
            throw new InvalidOperationException("Package content cannot be accepted.");
        SizeBytes = size; Sha256 = sha256; State = "Verifying"; Revision++;
    }
    public void Ready() { if (Disabled || SizeBytes is null) throw new InvalidOperationException("Package not verified."); State = "Ready"; Revision++; }
    public void Fail() { if (State != "Ready") { State = "Failed"; Revision++; } }
    public void Retry() { if (Disabled || SizeBytes is null || State != "Failed") throw new InvalidOperationException("Package not retryable."); State = "Verifying"; Revision++; }
    public void Stop() { Disabled = true; Revision++; }
}

public sealed class PackageWork : AggregateRoot<StrongId<PackageWork>>
{
    private PackageWork(StrongId<PackageWork> id) : base(id) { }
    public PackageWork(Guid id, Guid packageId, Guid softwareId, Guid initiator, DateTimeOffset now, string kind = "UploadVerificationAndCopy") : base(new(id))
    { PackageId = packageId; SoftwareId = softwareId; InitiatorId = initiator; CreatedAt = now; Kind = kind; }
    public Guid PackageId { get; private set; }
    public Guid SoftwareId { get; private set; }
    public Guid InitiatorId { get; private set; }
    public string Kind { get; private set; } = "";
    public string State { get; private set; } = "Pending";
    public string Stage { get; private set; } = "AwaitingUpload";
    public string SourceNode { get; private set; } = "";
    public Guid? ReceiveToken { get; private set; }
    public long ReceiveGeneration { get; private set; }
    public long DispatchSequence { get; private set; }
    public Guid DispatchEventId { get; private set; }
    public bool Accepted { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public long LeaseGeneration { get; private set; }
    public string? LeaseNode { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public long Revision { get; private set; } = 1;
    public void Begin(Guid token, string node)
    {
        if (Stage is not ("AwaitingUpload" or "Receiving" or "UploadFailed")) throw new InvalidOperationException("Upload is not open.");
        if (SourceNode.Length != 0 && SourceNode != node) throw new InvalidOperationException("Upload belongs to another node.");
        ReceiveToken = token; SourceNode = node; ReceiveGeneration++; Stage = "Receiving"; State = "Pending"; LastErrorCode = null; Revision++;
    }
    public void Dispatch(Guid initiator)
    { InitiatorId = initiator; DispatchSequence++; DispatchEventId = Guid.NewGuid(); Accepted = false; LeaseToken = null; LeaseUntil = null;
      State = "Pending"; Stage = "AwaitingReceipt"; LastErrorCode = null; Revision++; }
    public void RepairFrom(string node) { if (Kind != "Repair") throw new InvalidOperationException(); SourceNode = node; }
    public void Accept(long generation, Guid eventId)
    { if (generation != DispatchSequence || eventId != DispatchEventId) throw new InvalidOperationException("Stale work dispatch."); if (!Accepted) { Accepted = true; Stage = "Copying"; Revision++; } }
    public bool Claim(string node, Guid token, DateTimeOffset now, int seconds)
    {
        if (!Accepted || State is "Completed" or "Failed" || LeaseUntil > now) return false;
        LeaseToken = token; LeaseNode = node; LeaseGeneration++; LeaseUntil = now.AddSeconds(seconds); State = "Running"; Revision++; return true;
    }
    public bool Owns(Guid token, long generation, DateTimeOffset now) => LeaseToken == token && LeaseGeneration == generation && LeaseUntil > now && State == "Running";
    public void Renew(DateTimeOffset now, int seconds) { LeaseUntil = now.AddSeconds(seconds); Revision++; }
    public void Complete(DateTimeOffset now) { State = "Completed"; Stage = "Completed"; CompletedAt = now; LeaseUntil = null; Revision++; }
    public void Fail(string code, bool upload = false)
    { State = "Failed"; Stage = upload ? "UploadFailed" : "CopyFailed"; LastErrorCode = code; LeaseUntil = null; Revision++; }
    public void Stop() { State = "Failed"; Stage = "Stopped"; LastErrorCode = "RELEASE_DISABLED"; LeaseToken = null; LeaseUntil = null; Revision++; }
}

public sealed class PackageReplica(Guid id, Guid packageId, string nodeId)
{
    public Guid Id { get; private set; } = id;
    public Guid PackageId { get; private set; } = packageId;
    public string NodeId { get; private set; } = nodeId;
    public string State { get; private set; } = "Missing";
    public DateTimeOffset? CheckedAt { get; private set; }
    public void Check(string state, DateTimeOffset at) { State = state; CheckedAt = at; }
}
public sealed class PackageDownload(Guid id, Guid packageId, string nodeId, Guid workerGeneration,
    Guid subjectId, string actorKind, string? employeeNo, DateTimeOffset startedAt)
{
    public Guid Id { get; private set; } = id;
    public Guid PackageId { get; private set; } = packageId;
    public string NodeId { get; private set; } = nodeId;
    public Guid WorkerGeneration { get; private set; } = workerGeneration;
    public Guid SubjectId { get; private set; } = subjectId;
    public string ActorKind { get; private set; } = actorKind;
    public string? EmployeeNo { get; private set; } = employeeNo;
    public DateTimeOffset StartedAt { get; private set; } = startedAt;
    public DateTimeOffset? EndedAt { get; private set; }
    public long? BytesSent { get; private set; }
    public string State { get; private set; } = "Open";
    public bool End(string outcome, long bytes, DateTimeOffset at)
    { if (State != "Open") return false; State = outcome; BytesSent = bytes; EndedAt = at; return true; }
}
public interface IPackageRepository
{
    Task<PackageAsset?> GetAsync(Guid id, bool protect, CancellationToken token);
    Task<PackageWork?> WorkAsync(Guid id, bool protect, CancellationToken token);
    Task<PackageWork?> UploadAsync(Guid packageId, bool protect, CancellationToken token);
    Task<IReadOnlyList<PackageReplica>> ReplicasAsync(Guid packageId, bool protect, CancellationToken token);
    Task<IReadOnlyList<Guid>> PendingAsync(DateTimeOffset now, int take, CancellationToken token);
    Task<IReadOnlyList<Guid>> ReadyAsync(int take, Guid? after, CancellationToken token);
    Task<PackageDownload?> DownloadAsync(Guid id, bool protect, CancellationToken token);
    Task<bool> HasDispatchAsync(Guid workId, long dispatch, Guid eventId, CancellationToken token);
    Task<PackageReceiveAttempt?> AttemptAsync(Guid id, CancellationToken token);
    void Add(PackageAsset package, PackageWork work);
    void Add(PackageReplica replica);
    void Add(PackageWork work);
    void Add(PackageDownload download);
    void Add(PackageDispatch dispatch);
    void Add(PackageReceiveAttempt attempt);
}

public sealed record PackageDispatch(Guid Id, Guid WorkId, long Sequence);
public sealed record PackageReceiveAttempt(Guid Id, Guid UploadId, Guid PackageId, long Generation, string NodeId);
