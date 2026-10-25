using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;

namespace Svm.Application.Catalog;

internal sealed class CreateHardwareCommandAdapter(IHardwareAssets hardware) : IIdempotencyRequestAdapter<CreateHardwareCommand, OperationResult<HardwareView>>
{
    public OperationRequestData Describe(CreateHardwareCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("DeviceId", OperationValue.Identifier(x.DeviceId))), OperationValue.Object(new OperationField("Type", OperationValue.Text(x.Type)), new OperationField("Name", OperationValue.Text(x.Name)), new OperationField("AssetNumber", x.AssetNumber is null ? OperationValue.Null : OperationValue.Text(x.AssetNumber)), new OperationField("ManagementIp", x.ManagementIp is null ? OperationValue.Null : OperationValue.Text(x.ManagementIp))));
    public OperationResultReference GetReference(OperationResult<HardwareView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<HardwareView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await hardware.GetAsync(id, false, token);
        return OperationResult<HardwareView>.Completed(reference.OperationId, view, view.Id);
    }
}
internal sealed class UpdateHardwareCommandAdapter(IHardwareAssets hardware) : IIdempotencyRequestAdapter<UpdateHardwareCommand, OperationResult<HardwareView>>
{
    public OperationRequestData Describe(UpdateHardwareCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("HardwareId", OperationValue.Identifier(x.HardwareId))), OperationValue.Object(new OperationField("ExpectedRevision", OperationValue.Integer(x.ExpectedRevision)), new OperationField("Name", OperationValue.Text(x.Name)), new OperationField("AssetNumber", x.AssetNumber is null ? OperationValue.Null : OperationValue.Text(x.AssetNumber)), new OperationField("ManagementIp", x.ManagementIp is null ? OperationValue.Null : OperationValue.Text(x.ManagementIp)), new OperationField("Reason", OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<HardwareView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<HardwareView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await hardware.GetAsync(id, false, token);
        return OperationResult<HardwareView>.Completed(reference.OperationId, view, view.Id);
    }
}
internal sealed class CreateHardwareBindingCommandAdapter(IHardwareAssets hardware) : IIdempotencyRequestAdapter<CreateHardwareBindingCommand, OperationResult<HardwareBindingView>>
{
    public OperationRequestData Describe(CreateHardwareBindingCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("HardwareId", OperationValue.Identifier(x.HardwareId))), OperationValue.Object(new OperationField("SoftwareId", x.SoftwareId is { } sid ? OperationValue.Identifier(sid) : OperationValue.Null), new OperationField("NewSoftware", x.NewSoftware is { } n ? OperationValue.Object(new OperationField("Name", OperationValue.Text(n.Name)), new OperationField("Category", OperationValue.Text(n.Category)), new OperationField("Description", n.Description is null ? OperationValue.Null : OperationValue.Text(n.Description))) : OperationValue.Null), new OperationField("Reason", OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<HardwareBindingView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<HardwareBindingView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await hardware.BindingAsync(id, false, token);
        return OperationResult<HardwareBindingView>.Completed(reference.OperationId, view, view.Id);
    }
}
internal sealed class RevokeHardwareBindingCommandAdapter(IHardwareAssets hardware) : IIdempotencyRequestAdapter<RevokeHardwareBindingCommand, OperationResult<HardwareBindingView>>
{
    public OperationRequestData Describe(RevokeHardwareBindingCommand x) => new(x.Key,
        OperationValue.Object(new OperationField("HardwareId", OperationValue.Identifier(x.HardwareId)), new OperationField("SoftwareId", OperationValue.Identifier(x.SoftwareId))), OperationValue.Object(new OperationField("ExpectedRevision", OperationValue.Integer(x.ExpectedRevision)), new OperationField("Reason", OperationValue.Text(x.Reason))));
    public OperationResultReference GetReference(OperationResult<HardwareBindingView> x) => new(x.OperationId, x.Status, x.ResourceId);
    public async Task<OperationResult<HardwareBindingView>> RestoreAsync(OperationResultReference reference, CancellationToken token)
    {
        if (reference.Status != OperationStatus.Completed || reference.ResourceId is not { } id) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var view = await hardware.BindingAsync(id, false, token);
        return OperationResult<HardwareBindingView>.Completed(reference.OperationId, view, view.Id);
    }
}
