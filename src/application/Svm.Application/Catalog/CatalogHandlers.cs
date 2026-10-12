using MediatR;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Framework;

namespace Svm.Application.Catalog;

internal sealed class CatalogCompletion(IPersonnelService personnel, ISessionProofSource proof, IAuditWriter audit,
    ICallContext calls, IUnitOfWork unitOfWork)
{
    internal Task<OperationResult<T>> ExecuteAsync<T>(string operation, string reason, Func<Task<T>> action, Func<T, Guid> reference, CancellationToken token) =>
        ExecuteAsync(operation, reason, _ => action(), reference, token);
    internal async Task<OperationResult<T>> ExecuteAsync<T>(string operation, string reason, Func<PersonnelView, Task<T>> action, Func<T, Guid> reference, CancellationToken token)
    {
        var actor = await personnel.AuthenticateAsync(proof.Proof ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired), true, token)
            ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var value = await action(actor); var id = unitOfWork.CurrentOperationId ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var call = calls.Current ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var resource = reference(value);
        audit.Append(new(id, actor.SubjectId, "Human", actor.EmployeeNo, actor.DisplayName, operation, resource, "succeeded", reason, call.CorrelationId));
        return OperationResult<T>.Completed(id, value, resource);
    }
}

internal sealed class GetSiteQueryHandler(ISiteAssets assets, SiteCatalogOptions options) : IRequestHandler<GetSiteQuery, SiteView>
{
    public async Task<SiteView> Handle(GetSiteQuery request, CancellationToken token) { await assets.EnsureDeploymentAsync(false, token); return options.Require(); }
}
internal sealed class ListProcessesQueryHandler(ISiteAssetQueries queries) : IRequestHandler<ListProcessesQuery, CatalogPage<ProcessView>>
{
    public async Task<CatalogPage<ProcessView>> Handle(ListProcessesQuery request, CancellationToken token) { return await queries.ProcessesAsync(request.Input, token); }
}
internal sealed class GetProcessQueryHandler(ISiteAssetQueries queries) : IRequestHandler<GetProcessQuery, ProcessView>
{
    public async Task<ProcessView> Handle(GetProcessQuery request, CancellationToken token) { return await queries.ProcessAsync(request.ProcessId, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
}
internal sealed class ListDevicesQueryHandler(ISiteAssetQueries queries) : IRequestHandler<ListDevicesQuery, CatalogPage<DeviceView>>
{
    public async Task<CatalogPage<DeviceView>> Handle(ListDevicesQuery request, CancellationToken token) { return await queries.DevicesAsync(request.Input, token); }
}
internal sealed class GetDeviceQueryHandler(ISiteAssetQueries queries) : IRequestHandler<GetDeviceQuery, DeviceView>
{
    public async Task<DeviceView> Handle(GetDeviceQuery request, CancellationToken token) { return await queries.DeviceAsync(request.DeviceId, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
}
internal sealed class ListBindingsQueryHandler(ISiteAssetQueries queries, ISiteAssets assets) : IRequestHandler<ListBindingsQuery, CatalogPage<BindingView>>
{
    public async Task<CatalogPage<BindingView>> Handle(ListBindingsQuery request, CancellationToken token) { await assets.DeviceAsync(request.DeviceId, false, token); return await queries.BindingsAsync(request.DeviceId, request.Input, token); }
}
internal sealed class GetDeviceInventoryQueryHandler(ISiteAssetQueries queries, ISiteAssets assets) : IRequestHandler<GetDeviceInventoryQuery, CatalogPage<DeviceSoftwareInventoryItem>>
{
    public async Task<CatalogPage<DeviceSoftwareInventoryItem>> Handle(GetDeviceInventoryQuery request, CancellationToken token) { await assets.DeviceAsync(request.DeviceId, false, token); return await queries.InventoryAsync(request.DeviceId, request.Input, token); }
}
internal sealed class ListSoftwareQueryHandler(ISoftwareCatalogQueries queries) : IRequestHandler<ListSoftwareQuery, CatalogPage<SoftwareView>>
{
    public async Task<CatalogPage<SoftwareView>> Handle(ListSoftwareQuery request, CancellationToken token) { return await queries.ListAsync(request.Input, token); }
}
internal sealed class GetSoftwareQueryHandler(ISoftwareCatalogQueries queries) : IRequestHandler<GetSoftwareQuery, SoftwareView>
{
    public async Task<SoftwareView> Handle(GetSoftwareQuery request, CancellationToken token) { return await queries.GetAsync(request.SoftwareId, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
}
internal sealed class GetPermissionOptionsQueryHandler(ISoftwareCatalogQueries queries) : IRequestHandler<GetPermissionOptionsQuery, PermissionOptionsPage>
{
    public async Task<PermissionOptionsPage> Handle(GetPermissionOptionsQuery request, CancellationToken token) { return await queries.PermissionOptionsAsync(request.Input, token); }
}
internal sealed class CreateSoftwareCommandHandler(ISoftwareCatalog software, IPersonnelSoftwareAdministration grants, CatalogCompletion completion) : IRequestHandler<CreateSoftwareCommand, OperationResult<SoftwareView>>
{
    public Task<OperationResult<SoftwareView>> Handle(CreateSoftwareCommand request, CancellationToken token) => completion.ExecuteAsync("rel.software.create", "register software", async () => { var view = await software.CreateAsync(request.Code, request.Name, request.Category, request.Description, token); await grants.GrantCreatorAsync(view.Id, token); return view; }, x => x.Id, token);
}
internal sealed class UpdateSoftwareCommandHandler(ISoftwareCatalog software, CatalogCompletion completion) : IRequestHandler<UpdateSoftwareCommand, OperationResult<SoftwareView>>
{
    public Task<OperationResult<SoftwareView>> Handle(UpdateSoftwareCommand request, CancellationToken token) => completion.ExecuteAsync("rel.software.update", request.Reason, () => software.UpdateAsync(request.SoftwareId, request.ExpectedRevision, request.Name, request.Description, token), x => x.Id, token);
}
internal sealed class CreateProcessCommandHandler(ISiteAssets assets, CatalogCompletion completion) : IRequestHandler<CreateProcessCommand, OperationResult<ProcessView>>
{
    public Task<OperationResult<ProcessView>> Handle(CreateProcessCommand request, CancellationToken token) => completion.ExecuteAsync("ins.processes.create", "register process", () => assets.CreateProcessAsync(request.Code, request.Name, token), x => x.Id, token);
}
internal sealed class UpdateProcessCommandHandler(ISiteAssets assets, CatalogCompletion completion) : IRequestHandler<UpdateProcessCommand, OperationResult<ProcessView>>
{
    public Task<OperationResult<ProcessView>> Handle(UpdateProcessCommand request, CancellationToken token) => completion.ExecuteAsync("ins.processes.update", request.Reason, () => assets.UpdateProcessAsync(request.ProcessId, request.ExpectedRevision, request.Name, token), x => x.Id, token);
}
internal sealed class CreateDeviceCommandHandler(ISiteAssets assets, CatalogCompletion completion) : IRequestHandler<CreateDeviceCommand, OperationResult<DeviceView>>
{
    public Task<OperationResult<DeviceView>> Handle(CreateDeviceCommand request, CancellationToken token) => completion.ExecuteAsync("ins.devices.create", "register device", () => assets.CreateDeviceAsync(request.ProcessId, request.DeviceNo, request.Name, token), x => x.Id, token);
}
internal sealed class UpdateDeviceCommandHandler(ISiteAssets assets, CatalogCompletion completion) : IRequestHandler<UpdateDeviceCommand, OperationResult<DeviceView>>
{
    public Task<OperationResult<DeviceView>> Handle(UpdateDeviceCommand request, CancellationToken token) => completion.ExecuteAsync("ins.devices.update", request.Reason, () => assets.UpdateDeviceAsync(request.DeviceId, request.ExpectedRevision, request.ProcessId, request.Name, token), x => x.Id, token);
}
internal sealed class CreateBindingCommandHandler(ISiteAssets assets, CatalogCompletion completion) : IRequestHandler<CreateBindingCommand, OperationResult<BindingView>>
{
    public Task<OperationResult<BindingView>> Handle(CreateBindingCommand request, CancellationToken token) => completion.ExecuteAsync("ins.bindings.create", request.Reason, () => assets.CreateBindingAsync(request.DeviceId, request.SoftwareId, token), x => x.Id, token);
}
internal sealed class RevokeBindingCommandHandler(ISiteAssets assets, CatalogCompletion completion) : IRequestHandler<RevokeBindingCommand, OperationResult<BindingView>>
{
    public Task<OperationResult<BindingView>> Handle(RevokeBindingCommand request, CancellationToken token) => completion.ExecuteAsync("ins.bindings.revoke", request.Reason, () => assets.RevokeBindingAsync(request.DeviceId, request.SoftwareId, request.ExpectedRevision, token), x => x.Id, token);
}
