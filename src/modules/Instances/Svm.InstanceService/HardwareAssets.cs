using Svm.Core.Instances;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;

namespace Svm.InstanceService;

internal sealed class HardwareAssets(IHardwareRepository repository, ISiteAssetRepository sites,
    ISiteAssets assets, IUnitOfWork unit) : IHardwareAssets
{
    public async Task<HardwareView> GetAsync(Guid id, bool protect, CancellationToken token) => View(await Existing(id, protect, token));
    public async Task<HardwareBindingView> BindingAsync(Guid id, bool protect, CancellationToken token)
    {
        var snapshot = await repository.BindingAsync(id, false, token) ?? throw Reject(RequestFailure.ResourceNotFound);
        var hardware = await Existing(snapshot.HardwareId.Value, protect, token);
        var binding = protect ? await repository.BindingAsync(id, true, token) ?? throw Reject(RequestFailure.ResourceNotFound) : snapshot;
        return View(binding, hardware.DeviceId.Value);
    }
    public async Task<HardwareView> CreateAsync(Guid deviceId, string type, string name, string? assetNumber, string? managementIp, CancellationToken token)
    {
        RequireWrite(); await assets.DeviceAsync(deviceId, true, token);
        var value = new HardwareAsset(new(Guid.NewGuid()), new(deviceId), type, name, assetNumber, managementIp);
        repository.Add(value); return View(value);
    }
    public async Task<HardwareView> UpdateAsync(Guid id, long revision, string name, string? assetNumber, string? managementIp, CancellationToken token)
    {
        RequireWrite(); var value = await Existing(id, true, token); CheckRevision(value.Revision, revision);
        value.Update(name, assetNumber, managementIp); return View(value);
    }
    public async Task<HardwareBindingView> BindAsync(Guid hardwareId, Guid softwareId, CancellationToken token)
    {
        RequireWrite(); var h = await Existing(hardwareId, true, token);
        if (h.Type == "PLC" && await repository.HasActivePlcBindingAsync(hardwareId, token)) throw Reject(RequestFailure.InvalidState);
        var parent = await sites.BindingAsync(h.DeviceId.Value, softwareId, true, token);
        if (parent is null) { parent = new(new(Guid.NewGuid()), h.DeviceId, softwareId); sites.AddBinding(parent); }
        else if (!parent.IsActive) parent.Reactivate();
        var value = await repository.BindingAsync(hardwareId, softwareId, true, token);
        if (value is null) { value = new(new(Guid.NewGuid()), h.Id, parent.Id, softwareId, h.Type); repository.Add(value); }
        else { if (value.IsActive || value.InstanceId is not null) throw Reject(RequestFailure.InvalidState); value.Reactivate(); }
        return View(value, h.DeviceId.Value);
    }
    public async Task<HardwareBindingView> RevokeAsync(Guid hardwareId, Guid softwareId, long revision, CancellationToken token)
    {
        RequireWrite(); var h = await Existing(hardwareId, true, token);
        var value = await repository.BindingAsync(hardwareId, softwareId, true, token) ?? throw Reject(RequestFailure.ResourceNotFound);
        CheckRevision(value.Revision, revision);
        if (!value.IsActive || value.InstanceId is not null) throw Reject(RequestFailure.InvalidState);
        value.Revoke(); return View(value, h.DeviceId.Value);
    }
    public async Task<HardwareBindingView> VerifyPositionAsync(Guid id, Guid deviceId, Guid softwareId, long? revision, bool protect, CancellationToken token)
    {
        var value = await BindingAsync(id, protect, token);
        if (value.DeviceId != deviceId || value.SoftwareId != softwareId || !value.IsActive || value.InstanceId is not null)
            throw Reject(RequestFailure.InvalidState);
        if (revision is { } expected) CheckRevision(value.Revision, expected);
        var h = await GetAsync(value.HardwareId, false, token);
        if (h.Type != "IPC" || !await assets.HasActiveBindingAsync(deviceId, softwareId, token)) throw Reject(RequestFailure.InvalidState);
        return value;
    }
    public async Task ReferenceAsync(Guid id, Guid deviceId, Guid softwareId, Guid instanceId, long? revision, CancellationToken token)
    {
        RequireWrite(); await VerifyPositionAsync(id, deviceId, softwareId, revision, true, token);
        await assets.ConfirmInstanceReferenceAsync(deviceId, softwareId, token);
        var value = await repository.BindingAsync(id, true, token) ?? throw Reject(RequestFailure.ResourceNotFound);
        value.Reference(instanceId);
    }
    private async Task<HardwareAsset> Existing(Guid id, bool protect, CancellationToken token)
    {
        var snapshot = await repository.HardwareAsync(id, false, token) ?? throw Reject(RequestFailure.ResourceNotFound);
        await assets.DeviceAsync(snapshot.DeviceId.Value, protect, token);
        return protect ? await repository.HardwareAsync(id, true, token) ?? throw Reject(RequestFailure.ResourceNotFound) : snapshot;
    }
    private static HardwareView View(HardwareAsset h) => new(h.Id.Value, h.DeviceId.Value, h.Type, h.Name, h.AssetNumber, h.ManagementIp, h.Revision);
    private static HardwareBindingView View(HardwareSoftwareBinding b, Guid deviceId) => new(b.Id.Value, b.HardwareId.Value, deviceId, b.SoftwareId, b.IsActive, b.InstanceId, b.Revision);
    private static void CheckRevision(long actual, long expected) { if (actual != expected) throw Reject(RequestFailure.RevisionConflict); }
    private static RequestRejectedException Reject(RequestFailure failure) => new(failure);
    private void RequireWrite() { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
