namespace Svm.Core.Instances;

public sealed record InstanceTargetFilter(Guid SoftwareId, Guid? ProcessId, Guid? DeviceId, string? DeviceNo,
    string? ReportedIp, Guid? InstalledReleaseId, string? Freshness, string? RunningState, string? Lifecycle);
public interface IInstanceTargetRepository
{ Task<IReadOnlyList<Guid>> SnapshotPageAsync(InstanceTargetFilter filter, Guid? after, int take, CancellationToken token); }
