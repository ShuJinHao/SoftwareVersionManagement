using MediatR;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.Application.Catalog;

internal sealed class GetHardwareQueryHandler(IHardwareQueries queries) : IRequestHandler<GetHardwareQuery, HardwareView>
{
    public async Task<HardwareView> Handle(GetHardwareQuery x, CancellationToken token) =>
        await queries.GetAsync(x.HardwareId, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
}
internal sealed class ListHardwareQueryHandler(IHardwareQueries queries, ISiteAssets assets) : IRequestHandler<ListHardwareQuery, CatalogPage<HardwareView>>
{
    public async Task<CatalogPage<HardwareView>> Handle(ListHardwareQuery x, CancellationToken token)
    { await assets.DeviceAsync(x.DeviceId, false, token); return await queries.ListAsync(x.DeviceId, x.Input, token); }
}
internal sealed class ListHardwareBindingsQueryHandler(IHardwareQueries queries, IHardwareAssets hardware) : IRequestHandler<ListHardwareBindingsQuery, CatalogPage<HardwareBindingView>>
{
    public async Task<CatalogPage<HardwareBindingView>> Handle(ListHardwareBindingsQuery x, CancellationToken token)
    { await hardware.GetAsync(x.HardwareId, false, token); return await queries.BindingsAsync(x.HardwareId, x.Input, token); }
}
internal sealed class GetWorkspaceDevicesQueryHandler(IHardwareQueries queries) : IRequestHandler<GetWorkspaceDevicesQuery, WorkspacePage<WorkspaceDeviceView>>
{
    public Task<WorkspacePage<WorkspaceDeviceView>> Handle(GetWorkspaceDevicesQuery x, CancellationToken token) => queries.DevicesAsync(x.Input, token);
}
internal sealed class GetWorkspaceInstallationsQueryHandler(IHardwareQueries queries) : IRequestHandler<GetWorkspaceInstallationsQuery, WorkspacePage<WorkspaceInstallationView>>
{
    public Task<WorkspacePage<WorkspaceInstallationView>> Handle(GetWorkspaceInstallationsQuery x, CancellationToken token) => queries.InstallationsAsync(x.Input, token);
}
internal sealed class CreateHardwareCommandHandler(IHardwareAssets hardware, CatalogCompletion completion) : IRequestHandler<CreateHardwareCommand, OperationResult<HardwareView>>
{
    public Task<OperationResult<HardwareView>> Handle(CreateHardwareCommand x, CancellationToken token) => completion.ExecuteAsync("ins.hardware.create", "register hardware",
        () => hardware.CreateAsync(x.DeviceId, x.Type, x.Name, x.AssetNumber, x.ManagementIp, token), h => h.Id, token);
}
internal sealed class UpdateHardwareCommandHandler(IHardwareAssets hardware, CatalogCompletion completion) : IRequestHandler<UpdateHardwareCommand, OperationResult<HardwareView>>
{
    public Task<OperationResult<HardwareView>> Handle(UpdateHardwareCommand x, CancellationToken token) => completion.ExecuteAsync("ins.hardware.update", x.Reason,
        () => hardware.UpdateAsync(x.HardwareId, x.ExpectedRevision, x.Name, x.AssetNumber, x.ManagementIp, token), h => h.Id, token);
}
internal sealed class CreateHardwareBindingCommandHandler(IHardwareAssets hardware, ISoftwareCatalog software,
    CatalogCompletion completion) : IRequestHandler<CreateHardwareBindingCommand, OperationResult<HardwareBindingView>>
{
    public Task<OperationResult<HardwareBindingView>> Handle(CreateHardwareBindingCommand x, CancellationToken token) =>
        completion.ExecuteAsync("ins.hardware-bindings.create", x.Reason, async () =>
        {
            SoftwareView product;
            if (x.NewSoftware is { } draft)
            {
                product = await software.CreateAsync(null, draft.Name, draft.Category, draft.Description, token);
            }
            else product = await software.GetAsync(x.SoftwareId!.Value, true, token);
            if (product.Category is not ("UpperComputer" or "Vision")) throw new RequestRejectedException(RequestFailure.InvalidState);
            return await hardware.BindAsync(x.HardwareId, product.Id, token);
        }, h => h.Id, token);
}
internal sealed class RevokeHardwareBindingCommandHandler(IHardwareAssets hardware, CatalogCompletion completion) : IRequestHandler<RevokeHardwareBindingCommand, OperationResult<HardwareBindingView>>
{
    public Task<OperationResult<HardwareBindingView>> Handle(RevokeHardwareBindingCommand x, CancellationToken token) => completion.ExecuteAsync("ins.hardware-bindings.revoke", x.Reason,
        () => hardware.RevokeAsync(x.HardwareId, x.SoftwareId, x.ExpectedRevision, token), h => h.Id, token);
}
