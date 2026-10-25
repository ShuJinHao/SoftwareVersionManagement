using Svm.SharedKernel.Domain;

namespace Svm.Core.Instances;

public sealed class HardwareAsset(StrongId<HardwareAsset> id, StrongId<ProductionDevice> deviceId,
    string type, string name, string? assetNumber, string? managementIp) : AggregateRoot<StrongId<HardwareAsset>>(id)
{
    public StrongId<ProductionDevice> DeviceId { get; private set; } = deviceId;
    public string Type { get; private set; } = type is "IPC" or "PLC" ? type : throw new ArgumentException("Invalid hardware type.");
    public string Name { get; private set; } = Required(name);
    public string? AssetNumber { get; private set; } = assetNumber;
    public string? ManagementIp { get; private set; } = managementIp;
    public long Revision { get; private set; } = 1;
    public void Update(string name, string? assetNumber, string? managementIp)
    { Name = Required(name); AssetNumber = assetNumber; ManagementIp = managementIp; Revision++; }
    private static string Required(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
}

public sealed class HardwareSoftwareBinding(StrongId<HardwareSoftwareBinding> id,
    StrongId<HardwareAsset> hardwareId, StrongId<DeviceSoftwareBinding> deviceBindingId, Guid softwareId)
    : AggregateRoot<StrongId<HardwareSoftwareBinding>>(id)
{
    public StrongId<HardwareAsset> HardwareId { get; private set; } = hardwareId;
    public StrongId<DeviceSoftwareBinding> DeviceBindingId { get; private set; } = deviceBindingId;
    public Guid SoftwareId { get; private set; } = softwareId;
    public bool IsActive { get; private set; } = true;
    public long Revision { get; private set; } = 1;
    public Guid? InstanceId { get; private set; }
    public void Reactivate()
    { if (IsActive || InstanceId is not null) throw new InvalidOperationException("Position cannot be reactivated."); IsActive = true; Revision++; }
    public void Revoke()
    { if (!IsActive || InstanceId is not null) throw new InvalidOperationException("Referenced position cannot be revoked."); IsActive = false; Revision++; }
    public void Reference(Guid instanceId)
    {
        if (!IsActive || instanceId == Guid.Empty || InstanceId is not null) throw new InvalidOperationException("Position is unavailable.");
        InstanceId = instanceId; Revision++;
    }
}

public interface IHardwareRepository
{
    Task<HardwareAsset?> HardwareAsync(Guid id, bool protect, CancellationToken token);
    Task<HardwareSoftwareBinding?> BindingAsync(Guid id, bool protect, CancellationToken token);
    Task<HardwareSoftwareBinding?> BindingAsync(Guid hardwareId, Guid softwareId, bool protect, CancellationToken token);
    void Add(HardwareAsset hardware);
    void Add(HardwareSoftwareBinding binding);
}
