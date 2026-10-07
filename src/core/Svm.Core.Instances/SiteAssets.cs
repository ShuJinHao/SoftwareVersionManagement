using Svm.SharedKernel.Domain;

namespace Svm.Core.Instances;

public sealed class ProductionProcess(StrongId<ProductionProcess> id, Guid siteId, string code, string name)
    : AggregateRoot<StrongId<ProductionProcess>>(id)
{
    public Guid SiteId { get; private set; } = siteId != Guid.Empty ? siteId : throw new ArgumentException("A process needs a site.");
    public string Code { get; private set; } = Required(code);
    public string Name { get; private set; } = Required(name);
    public long Revision { get; private set; } = 1;
    public void Rename(string name) { Name = Required(name); Revision++; }
    private static string Required(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
}

public sealed class ProductionDevice(StrongId<ProductionDevice> id, StrongId<ProductionProcess> processId, string deviceNo, string name)
    : AggregateRoot<StrongId<ProductionDevice>>(id)
{
    public StrongId<ProductionProcess> ProcessId { get; private set; } = processId;
    public string DeviceNo { get; private set; } = Required(deviceNo);
    public string Name { get; private set; } = Required(name);
    public long Revision { get; private set; } = 1;
    public void Update(StrongId<ProductionProcess>? processId, string? name)
    { if (processId is { } process) ProcessId = process; if (name is not null) Name = Required(name); Revision++; }
    private static string Required(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
}

public sealed class DeviceSoftwareBinding(StrongId<DeviceSoftwareBinding> id, StrongId<ProductionDevice> deviceId, Guid softwareId)
    : AggregateRoot<StrongId<DeviceSoftwareBinding>>(id)
{
    public StrongId<ProductionDevice> DeviceId { get; private set; } = deviceId;
    public Guid SoftwareId { get; private set; } = softwareId != Guid.Empty ? softwareId : throw new ArgumentException("A binding needs software.");
    public long Revision { get; private set; } = 1;
    public bool IsActive { get; private set; } = true;
    public bool HasInstanceReference { get; private set; }
    public void Reactivate() { if (IsActive) throw new InvalidOperationException("Binding already active."); IsActive = true; Revision++; }
    public void Revoke()
    { if (!IsActive || HasInstanceReference) throw new InvalidOperationException("Binding cannot be revoked."); IsActive = false; Revision++; }
    // Called only by the INS registration facade in the same protected transaction. This batch has no registration entry point.
    public void ConfirmInstanceReference()
    { if (!IsActive) throw new InvalidOperationException("An inactive binding cannot be referenced."); HasInstanceReference = true; }
}

public interface ISiteAssetRepository
{
    Task EnsureSiteAsync(Guid siteId, bool write, CancellationToken token);
    Task<ProductionProcess?> ProcessAsync(Guid id, bool protect, CancellationToken token);
    Task<ProductionDevice?> DeviceAsync(Guid id, bool protect, CancellationToken token);
    Task<DeviceSoftwareBinding?> BindingAsync(Guid deviceId, Guid softwareId, bool protect, CancellationToken token);
    Task<DeviceSoftwareBinding?> BindingByIdAsync(Guid id, CancellationToken token);
    Task<bool> ProcessCodeExistsAsync(Guid siteId, string code, CancellationToken token);
    Task<bool> DeviceNoExistsAsync(string deviceNo, CancellationToken token);
    void AddProcess(ProductionProcess process);
    void AddDevice(ProductionDevice device);
    void AddBinding(DeviceSoftwareBinding binding);
}
