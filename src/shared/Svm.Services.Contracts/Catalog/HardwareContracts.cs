using Svm.Services.Contracts.Instances;

namespace Svm.Services.Contracts.Catalog;

public sealed record HardwareView(Guid Id, Guid DeviceId, string Type, string Name, string? AssetNumber,
    string? ManagementIp, long Revision);
public sealed record HardwareBindingView(Guid Id, Guid HardwareId, Guid DeviceId, Guid SoftwareId,
    bool IsActive, Guid? InstanceId, long Revision);
public sealed record NewHardwareSoftware(string Name, string Category, string? Description);
public sealed record WorkspaceDeviceView(DeviceView Device, int HardwareCount, int InstallationCount,
    int PendingLocationCount, int NeverReportedCount, int UnknownCount);
public sealed record WorkspaceInstallationView(Guid RowId, DeviceView Device, HardwareView? Hardware,
    HardwareBindingView? Position, SoftwareView Software, InstanceView? Instance, string LocationState)
{
    public Svm.Services.Contracts.Plc.PlcUsageSummary? PlcUsage { get; init; }
    public string? LatestFormalVersion { get; init; }
}
public sealed record HardwareFilter(Guid? ProcessId = null, Guid? DeviceId = null, Guid? HardwareId = null,
    Guid? SoftwareId = null, string? Name = null, string? Category = null);
public sealed record HardwareListInput(HardwareFilter Filter, int PageSize = 50, CatalogPosition? After = null);
public sealed record WorkspacePage<T>(IReadOnlyList<T> Items, CatalogPosition? Next, long TotalCount);

public interface IHardwareAssets
{
    Task<HardwareView> GetAsync(Guid id, bool protect, CancellationToken token);
    Task<HardwareBindingView> BindingAsync(Guid id, bool protect, CancellationToken token);
    Task<HardwareView> CreateAsync(Guid deviceId, string type, string name, string? assetNumber, string? managementIp, CancellationToken token);
    Task<HardwareView> UpdateAsync(Guid id, long revision, string name, string? assetNumber, string? managementIp, CancellationToken token);
    Task<HardwareBindingView> BindAsync(Guid hardwareId, Guid softwareId, CancellationToken token);
    Task<HardwareBindingView> RevokeAsync(Guid hardwareId, Guid softwareId, long revision, CancellationToken token);
    Task<HardwareBindingView> VerifyPositionAsync(Guid id, Guid deviceId, Guid softwareId, long? revision, bool protect, CancellationToken token);
    Task ReferenceAsync(Guid id, Guid deviceId, Guid softwareId, Guid instanceId, long? revision, CancellationToken token);
}
public interface IHardwareQueries
{
    Task<HardwareView?> GetAsync(Guid id, CancellationToken token);
    Task<CatalogPage<HardwareView>> ListAsync(Guid deviceId, HardwareListInput input, CancellationToken token);
    Task<CatalogPage<HardwareBindingView>> BindingsAsync(Guid hardwareId, HardwareListInput input, CancellationToken token);
    Task<WorkspacePage<WorkspaceDeviceView>> DevicesAsync(HardwareListInput input, CancellationToken token);
    Task<WorkspacePage<WorkspaceInstallationView>> InstallationsAsync(HardwareListInput input, CancellationToken token);
}
