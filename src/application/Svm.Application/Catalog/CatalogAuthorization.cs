using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Application.Personnel;

namespace Svm.Application.Catalog;

internal sealed class CatalogAuthorization(ISiteAssets assets, IHardwareAssets hardware, ISoftwareCatalog software,
    SiteAuthorization scope, IUnitOfWork unit)
{
    internal async ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, PersonnelView person, CancellationToken token)
    {
        var access = person.Access; var write = unit.CurrentOperationId is not null;
        await assets.EnsureDeploymentAsync(write, token);
        if (request.Request is GetPermissionOptionsQuery)
            return Decision(access.Global("identity.manage"));
        if (CatalogCapabilities.IsQuery(request.Request.GetType()))
        {
            if (request.Request is GetSoftwareQuery q)
                return await scope.SoftwareAsync(access, "software.read", q.SoftwareId, token)
                    ? AuthorizationDecision.Allow(AuthorizationTarget.Software(q.SoftwareId)) : Missing();
            // SQL applies the same range to items, counts and page boundaries; a query never loads all field data.
            return Decision(request.Request is ListSoftwareQuery || access.AnyField("asset.read"));
        }
        Guid? deviceId = request.Request switch
        {
            CreateDeviceCommand => null, UpdateDeviceCommand c => c.DeviceId, CreateHardwareCommand c => c.DeviceId,
            CreateBindingCommand c => c.DeviceId, RevokeBindingCommand c => c.DeviceId, _ => null
        };
        Guid? hardwareId = request.Request switch
        { UpdateHardwareCommand c => c.HardwareId, CreateHardwareBindingCommand c => c.HardwareId, RevokeHardwareBindingCommand c => c.HardwareId, _ => null };
        if (hardwareId is { } h) deviceId = (await hardware.GetAsync(h, false, token)).DeviceId;
        var softwareId = request.Request switch
        { UpdateSoftwareCommand c => c.SoftwareId, CreateBindingCommand c => c.SoftwareId, RevokeBindingCommand c => c.SoftwareId,
          CreateHardwareBindingCommand c => c.SoftwareId, RevokeHardwareBindingCommand c => c.SoftwareId, _ => (Guid?)null };
        bool allowed;
        switch (request.Request)
        {
            case CreateProcessCommand: allowed = access.Field("asset.create", null, null); break;
            case UpdateProcessCommand c: allowed = access.Field("asset.manage", c.ProcessId, null); break;
            case CreateDeviceCommand c: allowed = access.Field("asset.create", c.ProcessId, null); break;
            case CreateSoftwareCommand: allowed = access.Global("software.create"); break;
            case UpdateSoftwareCommand c: allowed = access.Product("release.upload", c.SoftwareId); break;
            default:
                var device = await assets.DeviceAsync(deviceId!.Value, false, token);
                var operation = request.Request is CreateHardwareCommand ? "asset.create" :
                    request.Request is CreateBindingCommand or RevokeBindingCommand or CreateHardwareBindingCommand or RevokeHardwareBindingCommand ? "binding.manage" : "asset.manage";
                allowed = access.Field(operation, device.ProcessId, device.Id, softwareId);
                if (request.Request is UpdateDeviceCommand { ProcessId: { } target } && target != device.ProcessId)
                    allowed &= access.Field("asset.manage", target, null);
                if (request.Request is CreateHardwareBindingCommand { NewSoftware: not null })
                    allowed = (allowed || access.Global("asset.manage")) && access.Field("software.create", device.ProcessId, device.Id);
                break;
        }
        if (!allowed) return Missing();
        if (softwareId is { } sid)
        {
            if (!await software.ExistsAsync(sid, write, token)) return Missing();
            if (request.Policy.Scope == RequestScope.Software) return AuthorizationDecision.Allow(AuthorizationTarget.Software(sid));
        }
        return AuthorizationDecision.Allow(AuthorizationTarget.Global());
    }
    private static AuthorizationDecision Decision(bool allowed) => allowed ? AuthorizationDecision.Allow(AuthorizationTarget.Global()) : AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
    private static AuthorizationDecision Missing() => AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
}
