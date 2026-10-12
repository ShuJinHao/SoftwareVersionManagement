using Svm.SharedKernel.Domain;

namespace Svm.Core.Instances;

public sealed class ManagedInstance(StrongId<ManagedInstance> id, Guid softwareId, Guid deviceId, Guid installationKey) : AggregateRoot<StrongId<ManagedInstance>>(id)
{
    public Guid SoftwareId { get; private set; } = softwareId;
    public Guid DeviceId { get; private set; } = deviceId;
    public Guid InstallationKey { get; private set; } = installationKey;
    public string Lifecycle { get; private set; } = "Active";
    public long Revision { get; private set; } = 1;
    public void SetLifecycle(string lifecycle) { if (lifecycle is not ("Active" or "Suspended")) throw new ArgumentException("Invalid lifecycle."); Lifecycle=lifecycle; Revision++; }
}
public sealed class InstanceSnapshot(StrongId<ManagedInstance> instanceId, Guid softwareId)
{
    public StrongId<ManagedInstance> InstanceId { get; private set; } = instanceId;
    public Guid SoftwareId { get; private set; } = softwareId;
    public long StreamEpoch { get; private set; }
    public bool StreamOpen { get; private set; }
    public long ReportSeq { get; private set; }
    public DateTimeOffset? LastAcceptedAt { get; private set; }
    public string? SnapshotJson { get; private set; }
    public string? RequestDigest { get; private set; }
    public string? InstallationDigest { get; private set; }
    public Guid? EvidenceId { get; private set; }
    public long Advance(bool open)
    { StreamEpoch=checked(StreamEpoch+1); StreamOpen=open; ReportSeq=0; RequestDigest=null; EvidenceId=null; return StreamEpoch; }
    public void Accept(long sequence, DateTimeOffset now, string snapshot, string digest, string installationDigest, Guid? evidenceId)
    { ReportSeq=sequence; LastAcceptedAt=now; SnapshotJson=snapshot; RequestDigest=digest; InstallationDigest=installationDigest; EvidenceId=evidenceId; }
}
public sealed class InstallationEvidence
{
    public Guid Id { get; init; }
    public StrongId<ManagedInstance> InstanceId { get; init; }
    public Guid SoftwareId { get; init; }
    public long StreamEpoch { get; init; }
    public long ReportSeq { get; init; }
    public string InstallationState { get; init; } = "Unknown";
    public Guid? InstalledReleaseId { get; init; }
    public string? InstalledVersion { get; init; }
    public DateTimeOffset? InstalledAt { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public string ReportedRunningState { get; init; } = "Unknown";
    public string RequestDigest { get; init; } = "";
}
public sealed class ReportStreamReceipt
{
    public Guid OperationId { get; init; }
    public StrongId<ManagedInstance> InstanceId { get; init; }
    public long Epoch { get; init; }
}
public interface IManagedInstanceRepository
{
    Task<InstallationEvidence?> EvidenceAsync(Guid id, CancellationToken token);
    Task<ManagedInstance?> GetAsync(Guid id, bool protect, CancellationToken token);
    Task<InstanceSnapshot> SnapshotAsync(Guid id, bool protect, CancellationToken token);
    Task<ReportStreamReceipt?> StreamReceiptAsync(Guid operationId, CancellationToken token);
    void Add(ManagedInstance instance, InstanceSnapshot snapshot);
    void AddEvidence(InstallationEvidence evidence);
    void AddStreamReceipt(ReportStreamReceipt receipt);
}
