using System.Text.Json;
using Svm.Core.Instances;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Tasks;

namespace Svm.InstanceService;

internal sealed class InstanceTaskFacts(IManagedInstanceRepository instances, IInstanceTargetRepository targets, IHardwareRepository hardware, ISiteAssetRepository assets) : IInstanceTaskFacts
{
    public async Task<InstanceTaskFact?> GetAsync(Guid id, bool protect, CancellationToken ct)
    { var i = await instances.GetAsync(id, protect, ct); if (i is null) return null; var s = await instances.SnapshotAsync(id, protect, ct);
      Guid? position = null;
      if (i.HardwareBindingId is { } bindingId)
      {
          var binding = await hardware.BindingAsync(bindingId, false, ct);
          var machine = binding is null ? null : await hardware.HardwareAsync(binding.HardwareId.Value, false, ct);
          if (binding is { IsActive: true } && binding.InstanceId == id && binding.SoftwareId == i.SoftwareId && machine?.DeviceId.Value == i.DeviceId && machine.Type == "IPC" &&
              (await assets.BindingAsync(i.DeviceId, i.SoftwareId, false, ct))?.IsActive == true) position = bindingId;
      }
      return new(id, i.SoftwareId, i.Lifecycle, s.SnapshotJson is null ? null : JsonSerializer.Deserialize<StateReport>(s.SnapshotJson), position); }
    public Task<IReadOnlyList<Guid>> SnapshotPageAsync(InstanceFilter f, Guid subjectId, Guid? after, int take, CancellationToken ct) => targets.SnapshotPageAsync(new(f.SoftwareId, f.ProcessId, f.DeviceId, f.DeviceNo, f.ReportedIp, f.InstalledReleaseId, f.Freshness, f.RunningState, f.Lifecycle), subjectId, after, take, ct);
}
