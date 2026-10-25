using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;

namespace Svm.Application.Personnel;
internal sealed class SiteAuthorization(ISiteAssets assets, ISiteScopeFacts facts)
{
    internal async Task<bool> DeviceAsync(AuthorizationView access, string operation, Guid deviceId, Guid? softwareId, CancellationToken token)
    {
        var device = await assets.DeviceAsync(deviceId, false, token);
        return access.Field(operation, device.ProcessId, device.Id, softwareId);
    }
    internal async Task<bool> SoftwareAsync(AuthorizationView access, string operation, Guid softwareId, CancellationToken token)
    {
        if (access.Product(operation, softwareId)) return true;
        if (!RoleOperationCatalog.Field.Contains(operation)) return false;
        var scopes = access.FieldScopes(operation);
        if (scopes.Count == 0) return false;
        return await facts.HasSoftwareAsync(softwareId, scopes.Any(s => s.Kind == "Site"),
            scopes.Where(s => s.Kind == "Process").Select(s => s.ResourceId!.Value).ToArray(),
            scopes.Where(s => s.Kind == "Device").Select(s => s.ResourceId!.Value).ToArray(), token);
    }
}
