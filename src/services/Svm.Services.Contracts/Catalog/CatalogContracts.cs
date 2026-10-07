using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Services.Contracts.Catalog;

public sealed record SiteCatalogOptions(Guid? SiteId = null, string? SiteName = null, string? SiteTimeZone = null,
    int DefaultPageSize = 50, int MaximumPageSize = 200, int CursorMinutes = 15)
{
    public void Validate()
    {
        if (SiteId is null || SiteId == Guid.Empty || string.IsNullOrWhiteSpace(SiteName) || SiteName.Length > 128 ||
            string.IsNullOrWhiteSpace(SiteTimeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(SiteTimeZone, out _) ||
            DefaultPageSize < 1 || MaximumPageSize < DefaultPageSize || MaximumPageSize > 200 || CursorMinutes is < 1 or > 60)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    public SiteView Require() { Validate(); return new(SiteId!.Value, SiteName!, SiteTimeZone!); }
}
public sealed record SiteView(Guid SiteId, string SiteName, string SiteTimeZone);
public sealed record SoftwareView(Guid Id, string Code, string Name, string Category, string? Description,
    Guid? LatestAvailableFormalReleaseId, long Revision);
public sealed record ProcessView(Guid Id, Guid SiteId, string Code, string Name, long Revision);
public sealed record DeviceView(Guid Id, string DeviceNo, string Name, Guid ProcessId, string ProcessCode,
    string ProcessName, Guid SiteId, string SiteName, long Revision);
// Id and IsActive support safe internal result reconstruction; HTTP projects the documented public fields.
public sealed record BindingView(Guid Id, Guid DeviceId, Guid SoftwareId, long Revision, bool IsActive);
public sealed record DeviceSoftwareInventoryItem(SoftwareView Software, BindingView Binding);
public sealed record CatalogPosition(string SortKey, Guid Id);
public sealed record CatalogFilter(string? Code = null, string? Name = null, string? Category = null, Guid? ProcessId = null, Guid? SoftwareId = null);
public sealed record CatalogListInput(CatalogFilter Filter, int PageSize, CatalogPosition? After = null);
public sealed record CatalogPage<T>(IReadOnlyList<T> Items, CatalogPosition? Next);
public sealed record PermissionOptionsPage(IReadOnlyList<SoftwareView> Items, IReadOnlyList<string> SoftwareOperations, CatalogPosition? Next);

public interface ISoftwareCatalog
{
    Task<bool> ExistsAsync(Guid softwareId, bool protect, CancellationToken token);
    Task<SoftwareView> GetAsync(Guid softwareId, bool protect, CancellationToken token);
    Task<SoftwareView> CreateAsync(string code, string name, string category, string? description, CancellationToken token);
    Task<SoftwareView> UpdateAsync(Guid softwareId, long revision, string name, string? description, CancellationToken token);
}
public interface ISiteAssets
{
    Task EnsureDeploymentAsync(bool write, CancellationToken token);
    Task<ProcessView> ProcessAsync(Guid id, bool protect, CancellationToken token);
    Task<DeviceView> DeviceAsync(Guid id, bool protect, CancellationToken token);
    Task<ProcessView> CreateProcessAsync(string code, string name, CancellationToken token);
    Task<ProcessView> UpdateProcessAsync(Guid id, long revision, string name, CancellationToken token);
    Task<DeviceView> CreateDeviceAsync(Guid processId, string deviceNo, string name, CancellationToken token);
    Task<DeviceView> UpdateDeviceAsync(Guid id, long revision, Guid? processId, string? name, CancellationToken token);
    Task<BindingView> CreateBindingAsync(Guid deviceId, Guid softwareId, CancellationToken token);
    Task<BindingView> RevokeBindingAsync(Guid deviceId, Guid softwareId, long revision, CancellationToken token);
    Task<BindingView> BindingResultAsync(Guid id, CancellationToken token);
    Task ConfirmInstanceReferenceAsync(Guid deviceId, Guid softwareId, CancellationToken token);
}
public interface ISoftwareCatalogQueries
{
    Task<SoftwareView?> GetAsync(Guid id, CancellationToken token);
    Task<CatalogPage<SoftwareView>> ListAsync(CatalogListInput input, CancellationToken token);
    Task<PermissionOptionsPage> PermissionOptionsAsync(CatalogListInput input, CancellationToken token);
}
public interface ISiteAssetQueries
{
    Task<CatalogPage<ProcessView>> ProcessesAsync(CatalogListInput input, CancellationToken token);
    Task<ProcessView?> ProcessAsync(Guid id, CancellationToken token);
    Task<CatalogPage<DeviceView>> DevicesAsync(CatalogListInput input, CancellationToken token);
    Task<DeviceView?> DeviceAsync(Guid id, CancellationToken token);
    Task<CatalogPage<BindingView>> BindingsAsync(Guid deviceId, CatalogListInput input, CancellationToken token);
    Task<CatalogPage<DeviceSoftwareInventoryItem>> InventoryAsync(Guid deviceId, CatalogListInput input, CancellationToken token);
}
public interface IPersonnelSoftwareAdministration
{
    Task<UserView> ReplaceAsync(Guid userId, long revision, IReadOnlyList<PermissionView> permissions, CancellationToken token);
    Task GrantCreatorAsync(Guid softwareId, CancellationToken token);
}
public static class CatalogCapabilities
{
    public static bool IsWrite(Type type) => type == typeof(CreateSoftwareCommand) || type == typeof(UpdateSoftwareCommand) ||
        type == typeof(CreateProcessCommand) || type == typeof(UpdateProcessCommand) || type == typeof(CreateDeviceCommand) ||
        type == typeof(UpdateDeviceCommand) || type == typeof(CreateBindingCommand) || type == typeof(RevokeBindingCommand);
    public static bool IsQuery(Type type) => type == typeof(GetSiteQuery) || type == typeof(ListSoftwareQuery) || type == typeof(GetSoftwareQuery) ||
        type == typeof(ListProcessesQuery) || type == typeof(GetProcessQuery) || type == typeof(ListDevicesQuery) || type == typeof(GetDeviceQuery) ||
        type == typeof(ListBindingsQuery) || type == typeof(GetDeviceInventoryQuery) || type == typeof(GetPermissionOptionsQuery);
    public static Guid? SoftwareTarget(object request) => request switch
    { GetSoftwareQuery x => x.SoftwareId, UpdateSoftwareCommand x => x.SoftwareId,
      CreateBindingCommand x => x.SoftwareId, RevokeBindingCommand x => x.SoftwareId, _ => null };
}
