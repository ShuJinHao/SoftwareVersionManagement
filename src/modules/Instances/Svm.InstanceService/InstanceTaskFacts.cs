using System.Text.Json;
using Svm.Core.Instances;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Tasks;

namespace Svm.InstanceService;

internal sealed class InstanceTaskFacts(IManagedInstanceRepository instances, IInstanceTargetRepository targets) : IInstanceTaskFacts
{
    public async Task<InstanceTaskFact?> GetAsync(Guid id, bool protect, CancellationToken ct)
    { var i = await instances.GetAsync(id, protect, ct); if (i is null) return null; var s = await instances.SnapshotAsync(id, protect, ct);
      return new(id, i.SoftwareId, i.Lifecycle, s.SnapshotJson is null ? null : JsonSerializer.Deserialize<StateReport>(s.SnapshotJson)); }
    public Task<IReadOnlyList<Guid>> SnapshotPageAsync(InstanceFilter f, Guid? after, int take, CancellationToken ct) => targets.SnapshotPageAsync(new(f.SoftwareId, f.ProcessId, f.DeviceId, f.DeviceNo, f.ReportedIp, f.InstalledReleaseId, f.Freshness, f.RunningState, f.Lifecycle), after, take, ct);
}
