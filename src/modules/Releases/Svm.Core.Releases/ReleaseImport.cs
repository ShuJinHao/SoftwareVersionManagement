namespace Svm.Core.Releases;

public sealed class ReleaseSourceBinding
{
    private ReleaseSourceBinding() { }
    public ReleaseSourceBinding(Guid id, Guid softwareId, Guid subjectId, string address, long projectId, string packageName)
    { Id = id; SoftwareId = softwareId; SubjectId = subjectId; BaseUri = address; ProjectId = projectId; PackageName = packageName; }
    public Guid Id { get; private set; }
    public Guid SoftwareId { get; private set; }
    public Guid SubjectId { get; private set; }
    public string BaseUri { get; private set; } = "";
    public long ProjectId { get; private set; }
    public string PackageName { get; private set; } = "";
    public Guid? ConnectionId { get; private set; }
    public bool Managed { get; private set; }
    public bool IsCurrent { get; private set; } = true;
    public bool Enabled { get; private set; } = true;
    public long Revision { get; private set; } = 1;
    public long SourceRevision { get; private set; } = 1;
    public void Manage(Guid connectionId, bool enabled, long revision)
    { ConnectionId = connectionId; Managed = true; Enabled = enabled; Revision = revision; SourceRevision = revision; }
    public void SetEnabled(bool enabled) { Enabled = enabled; Revision++; }
    public void Retire() { IsCurrent = false; }
    public bool Initialized { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }
    public DateTimeOffset? NextScanAt { get; private set; }
    public Guid? LatestWorkId { get; private set; }
    public string? LastErrorCode { get; private set; }
    public void Scheduled(Guid workId, DateTimeOffset next) { LatestWorkId = workId; NextScanAt = next; }
    public void Track(Guid workId) => LatestWorkId = workId;
    public void Scanned(DateTimeOffset now, bool initialized) { LastSyncedAt = now; Initialized |= initialized; LastErrorCode = null; }
    public void Error(string code) => LastErrorCode = code;
}

public sealed class ReleaseSyncWork
{
    private ReleaseSyncWork() { }
    public ReleaseSyncWork(Guid id, Guid bindingId, Guid softwareId, string? tag, bool initialScan, DateTimeOffset now)
    { Id = id; BindingId = bindingId; SoftwareId = softwareId; Tag = tag; Kind = tag is null ? "Scan" : "Import"; InitialScan = initialScan; CreatedAt = now; NextAttemptAt = now; }
    public Guid Id { get; private set; }
    public Guid BindingId { get; private set; }
    public Guid SoftwareId { get; private set; }
    public string Kind { get; private set; } = "";
    public string? Tag { get; private set; }
    public string Stage { get; private set; } = "Pending";
    public int Page { get; private set; } = 1;
    public string? CandidateJson { get; private set; }
    public bool InitialScan { get; private set; }
    public Guid? ReleaseId { get; private set; }
    public Guid? ReceiveToken { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public long LeaseGeneration { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public bool Claim(Guid token, DateTimeOffset now, int seconds)
    {
        if (Stage is "Completed" or "Conflict" or "Historical" || NextAttemptAt > now || LeaseUntil > now) return false;
        LeaseToken = token; LeaseGeneration++; LeaseUntil = now.AddSeconds(seconds); Revision++; return true;
    }
    public bool Owns(Guid token, long generation, DateTimeOffset now) => LeaseToken == token && LeaseGeneration == generation && LeaseUntil > now;
    public void Renew(DateTimeOffset until) { LeaseUntil = until; Revision++; }
    public void Scan(int page, string? candidate) { Page = page; CandidateJson = candidate; Stage = "Scanning"; Revision++; }
    public void Registered(Guid releaseId, string facts) { ReleaseId = releaseId; CandidateJson = facts; Stage = "Downloading"; Revision++; }
    public void Receiving(Guid token) { ReceiveToken = token; Revision++; }
    public void ObserveHistory() { Stage = "Historical"; Revision++; }
    public void Received(DateTimeOffset now) { Stage = "PackageProcessing"; ReleaseLease(now); }
    public void Complete(DateTimeOffset now) { Stage = "Completed"; CompletedAt = now; LastErrorCode = null; ReleaseLease(now); }
    public void Retry(DateTimeOffset now)
    { if (LeaseUntil > now || Stage == "Conflict") return; CompletedAt = null; Stage = ReleaseId is null ? "Pending" : "VerifyingSource"; ReleaseLease(now); }
    public void Delay(string code, DateTimeOffset next, bool conflict)
    { LastErrorCode = code; if (conflict) Stage = "Conflict"; ReleaseLease(next); }
    private void ReleaseLease(DateTimeOffset next) { LeaseToken = null; LeaseUntil = null; NextAttemptAt = next; Revision++; }
}

public interface IReleaseImportRepository
{
    Task<ReleaseSourceBinding?> BindingByIdAsync(Guid bindingId, bool protect, CancellationToken token);
    Task<IReadOnlyList<ReleaseSourceBinding>> BindingsAsync(Guid? afterId, int count, CancellationToken token);
    Task<IReadOnlyList<Guid>> BlockingWorkIdsAsync(Guid bindingId, CancellationToken token);
    Task<ReleaseSourceChange?> ChangeAsync(Guid changeId, CancellationToken token);
    void Add(ReleaseSourceChange change);
    Task<ReleaseSourceBinding?> BindingAsync(Guid softwareId, bool protect, CancellationToken token);
    Task<ReleaseSyncWork?> WorkAsync(Guid id, bool protect, CancellationToken token);
    Task<ReleaseSyncWork?> FindAsync(Guid bindingId, string? tag, bool activeOnly, CancellationToken token);
    Task<IReadOnlyList<Guid>> PendingAsync(Guid softwareId, DateTimeOffset now, CancellationToken token);
    Task<SoftwareRelease?> VersionAsync(Guid softwareId, int major, int minor, int patch, CancellationToken token);
    Task<SoftwareRelease?> TagAsync(Guid bindingId, string tag, CancellationToken token);
    void Add(ReleaseSourceBinding binding);
    void Add(ReleaseSyncWork work);
}

// Stable, non-secret result reference: replays never modify the current binding.
public sealed class ReleaseSourceChange
{
    private ReleaseSourceChange() { }
    public ReleaseSourceChange(Guid id, Guid bindingId, long revision, bool enabled, DateTimeOffset createdAt)
    { Id = id; BindingId = bindingId; Revision = revision; Enabled = enabled; CreatedAt = createdAt; }
    public Guid Id { get; private set; }
    public Guid BindingId { get; private set; }
    public long Revision { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
