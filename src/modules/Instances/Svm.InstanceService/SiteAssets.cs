using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Instances;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.InstanceService;

public static class SiteAssetRegistration
{
    public static IServiceCollection AddSvmSiteAssets(this IServiceCollection services) => services.AddScoped<ISiteAssets, SiteAssets>();
}
internal sealed class SiteAssets(ISiteAssetRepository repository, IUnitOfWork unitOfWork, SiteCatalogOptions options) : ISiteAssets
{
    public Task EnsureDeploymentAsync(bool write, CancellationToken token)
    { if (write) RequireWrite(); return repository.EnsureSiteAsync(options.Require().SiteId, write, token); }
    public async Task<ProcessView> ProcessAsync(Guid id, bool protect, CancellationToken token) => View(await ExistingProcess(id, protect, token));
    public async Task<DeviceView> DeviceAsync(Guid id, bool protect, CancellationToken token) => await View(await ExistingDevice(id, protect, token), token);
    public async Task<ProcessView> CreateProcessAsync(string code, string name, CancellationToken token)
    {
        RequireWrite(); var site = options.Require();
        if (await repository.ProcessCodeExistsAsync(site.SiteId, code, token)) throw new RequestRejectedException(RequestFailure.InvalidState);
        var value = new ProductionProcess(new(Guid.NewGuid()), site.SiteId, code, name); repository.AddProcess(value); return View(value);
    }
    public async Task<ProcessView> UpdateProcessAsync(Guid id, long revision, string name, CancellationToken token)
    { RequireWrite(); var value = await ExistingProcess(id, true, token); CheckRevision(value.Revision, revision); value.Rename(name); return View(value); }
    public async Task<DeviceView> CreateDeviceAsync(Guid processId, string deviceNo, string name, CancellationToken token)
    {
        RequireWrite(); var process = await ExistingProcess(processId, true, token);
        if (await repository.DeviceNoExistsAsync(deviceNo, token)) throw new RequestRejectedException(RequestFailure.InvalidState);
        var value = new ProductionDevice(new(Guid.NewGuid()), process.Id, deviceNo, name); repository.AddDevice(value); return await View(value, token);
    }
    public async Task<DeviceView> UpdateDeviceAsync(Guid id, long revision, Guid? processId, string? name, CancellationToken token)
    {
        RequireWrite();
        // Resolve before locks, then protect both old/new processes in ID order before the device.
        var snapshot = await ExistingDevice(id, false, token);
        foreach (var process in new[] { snapshot.ProcessId.Value, processId ?? snapshot.ProcessId.Value }.Distinct().Order())
            await ExistingProcess(process, true, token);
        var value = await ExistingDevice(id, true, token);
        CheckRevision(value.Revision, revision);
        if (value.ProcessId != snapshot.ProcessId) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        value.Update(processId is { } target ? new StrongId<ProductionProcess>(target) : null, name);
        return await View(value, token);
    }
    public async Task<BindingView> CreateBindingAsync(Guid deviceId, Guid softwareId, CancellationToken token)
    {
        RequireWrite(); await ExistingDevice(deviceId, true, token);
        var value = await repository.BindingAsync(deviceId, softwareId, true, token);
        if (value is null) { value = new(new(Guid.NewGuid()), new(deviceId), softwareId); repository.AddBinding(value); }
        else if (value.IsActive) throw new RequestRejectedException(RequestFailure.InvalidState);
        else value.Reactivate();
        return View(value);
    }
    public async Task<BindingView> RevokeBindingAsync(Guid deviceId, Guid softwareId, long revision, CancellationToken token)
    {
        RequireWrite(); await ExistingDevice(deviceId, true, token);
        var value = await repository.BindingAsync(deviceId, softwareId, true, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        CheckRevision(value.Revision, revision);
        if (!value.IsActive || value.HasInstanceReference) throw new RequestRejectedException(RequestFailure.InvalidState);
        value.Revoke(); return View(value);
    }
    public async Task<BindingView> BindingResultAsync(Guid id, CancellationToken token)
    {
        var value = await repository.BindingByIdAsync(id, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        await ExistingDevice(value.DeviceId.Value, false, token); return View(value);
    }
    public async Task<bool> HasActiveBindingAsync(Guid deviceId, Guid softwareId, CancellationToken token) =>
        (await repository.BindingAsync(deviceId, softwareId, unitOfWork.CurrentOperationId is not null, token))?.IsActive == true;
    public async Task ConfirmInstanceReferenceAsync(Guid deviceId, Guid softwareId, CancellationToken token)
    {
        RequireWrite(); await ExistingDevice(deviceId, true, token);
        var value = await repository.BindingAsync(deviceId, softwareId, true, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (!value.IsActive) throw new RequestRejectedException(RequestFailure.InvalidState);
        value.ConfirmInstanceReference();
    }
    private async Task<ProductionProcess> ExistingProcess(Guid id, bool protect, CancellationToken token)
    {
        var value = await repository.ProcessAsync(id, protect, token);
        return value is not null && value.SiteId == options.Require().SiteId ? value : throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    }
    private async Task<ProductionDevice> ExistingDevice(Guid id, bool protect, CancellationToken token)
    { var value = await repository.DeviceAsync(id, protect, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); await ExistingProcess(value.ProcessId.Value, false, token); return value; }
    private ProcessView View(ProductionProcess x) => new(x.Id.Value, x.SiteId, x.Code, x.Name, x.Revision);
    private async Task<DeviceView> View(ProductionDevice x, CancellationToken token)
    { var p = await ExistingProcess(x.ProcessId.Value, false, token); return new(x.Id.Value, x.DeviceNo, x.Name, p.Id.Value, p.Code, p.Name, p.SiteId, options.Require().SiteName, x.Revision); }
    private static BindingView View(DeviceSoftwareBinding x) => new(x.Id.Value, x.DeviceId.Value, x.SoftwareId, x.Revision, x.IsActive);
    private static void CheckRevision(long actual, long expected) { if (actual != expected) throw new RequestRejectedException(RequestFailure.RevisionConflict); }
    private void RequireWrite() { options.Require(); if (unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
