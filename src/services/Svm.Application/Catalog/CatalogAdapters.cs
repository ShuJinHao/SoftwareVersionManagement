using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;

namespace Svm.Application.Catalog;

internal sealed class CreateSoftwareCommandAdapter(ISoftwareCatalog software) : IIdempotencyRequestAdapter<CreateSoftwareCommand, OperationResult<SoftwareView>>
{
    public OperationRequestData Describe(CreateSoftwareCommand x) => new(x.Key,
        OperationValue.Object(),
        OperationValue.Object(new OperationField("code", OperationValue.Text(x.Code)), new OperationField("name", OperationValue.Text(x.Name)), new OperationField("category", OperationValue.Text(x.Category)), new OperationField("description", x.Description is null ? OperationValue.Null : OperationValue.Text(x.Description))));
    public OperationResultReference GetReference(OperationResult<SoftwareView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<SoftwareView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await software.GetAsync(id, false, token);
        return OperationResult<SoftwareView>.Completed(reference.OperationId, view, view.Id);
    }
}

internal sealed class UpdateSoftwareCommandAdapter(ISoftwareCatalog software) : IIdempotencyRequestAdapter<UpdateSoftwareCommand, OperationResult<SoftwareView>>
{
    public OperationRequestData Describe(UpdateSoftwareCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("softwareId", OperationValue.Identifier(x.SoftwareId))),
        OperationValue.Object(new OperationField("expectedRevision", OperationValue.Integer(x.ExpectedRevision)), new OperationField("name", OperationValue.Text(x.Name)), new OperationField("description", x.Description is null ? OperationValue.Null : OperationValue.Text(x.Description)), new OperationField("reason", OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<SoftwareView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<SoftwareView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await software.GetAsync(id, false, token);
        return OperationResult<SoftwareView>.Completed(reference.OperationId, view, view.Id);
    }
}

internal sealed class CreateProcessCommandAdapter(ISiteAssets assets) : IIdempotencyRequestAdapter<CreateProcessCommand, OperationResult<ProcessView>>
{
    public OperationRequestData Describe(CreateProcessCommand x) => new(x.Key,
        OperationValue.Object(),
        OperationValue.Object(new OperationField("code", OperationValue.Text(x.Code)), new OperationField("name", OperationValue.Text(x.Name))));
    public OperationResultReference GetReference(OperationResult<ProcessView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<ProcessView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await assets.ProcessAsync(id, false, token);
        return OperationResult<ProcessView>.Completed(reference.OperationId, view, view.Id);
    }
}

internal sealed class UpdateProcessCommandAdapter(ISiteAssets assets) : IIdempotencyRequestAdapter<UpdateProcessCommand, OperationResult<ProcessView>>
{
    public OperationRequestData Describe(UpdateProcessCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("processId", OperationValue.Identifier(x.ProcessId))),
        OperationValue.Object(new OperationField("expectedRevision", OperationValue.Integer(x.ExpectedRevision)), new OperationField("name", OperationValue.Text(x.Name)), new OperationField("reason", OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<ProcessView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<ProcessView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await assets.ProcessAsync(id, false, token);
        return OperationResult<ProcessView>.Completed(reference.OperationId, view, view.Id);
    }
}

internal sealed class CreateDeviceCommandAdapter(ISiteAssets assets) : IIdempotencyRequestAdapter<CreateDeviceCommand, OperationResult<DeviceView>>
{
    public OperationRequestData Describe(CreateDeviceCommand x) => new(x.Key,
        OperationValue.Object(),
        OperationValue.Object(new OperationField("processId", OperationValue.Identifier(x.ProcessId)), new OperationField("deviceNo", OperationValue.Text(x.DeviceNo)), new OperationField("name", OperationValue.Text(x.Name))));
    public OperationResultReference GetReference(OperationResult<DeviceView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<DeviceView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await assets.DeviceAsync(id, false, token);
        return OperationResult<DeviceView>.Completed(reference.OperationId, view, view.Id);
    }
}

internal sealed class UpdateDeviceCommandAdapter(ISiteAssets assets) : IIdempotencyRequestAdapter<UpdateDeviceCommand, OperationResult<DeviceView>>
{
    public OperationRequestData Describe(UpdateDeviceCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("deviceId", OperationValue.Identifier(x.DeviceId))),
        OperationValue.Object(new OperationField("expectedRevision", OperationValue.Integer(x.ExpectedRevision)), new OperationField("processId", x.ProcessId is { } processid ? OperationValue.Identifier(processid) : OperationValue.Null), new OperationField("name", x.Name is null ? OperationValue.Null : OperationValue.Text(x.Name)), new OperationField("reason", OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<DeviceView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<DeviceView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await assets.DeviceAsync(id, false, token);
        return OperationResult<DeviceView>.Completed(reference.OperationId, view, view.Id);
    }
}

internal sealed class CreateBindingCommandAdapter(ISiteAssets assets) : IIdempotencyRequestAdapter<CreateBindingCommand, OperationResult<BindingView>>
{
    public OperationRequestData Describe(CreateBindingCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("deviceId", OperationValue.Identifier(x.DeviceId))),
        OperationValue.Object(new OperationField("softwareId", OperationValue.Identifier(x.SoftwareId)), new OperationField("reason", OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<BindingView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<BindingView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await assets.BindingResultAsync(id, token);
        return OperationResult<BindingView>.Completed(reference.OperationId, view, view.Id);
    }
}

internal sealed class RevokeBindingCommandAdapter(ISiteAssets assets) : IIdempotencyRequestAdapter<RevokeBindingCommand, OperationResult<BindingView>>
{
    public OperationRequestData Describe(RevokeBindingCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("deviceId", OperationValue.Identifier(x.DeviceId)), new OperationField("softwareId", OperationValue.Identifier(x.SoftwareId))),
        OperationValue.Object(new OperationField("expectedRevision", OperationValue.Integer(x.ExpectedRevision)), new OperationField("reason", OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<BindingView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<BindingView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await assets.BindingResultAsync(id, token);
        return OperationResult<BindingView>.Completed(reference.OperationId, view, view.Id);
    }
}
