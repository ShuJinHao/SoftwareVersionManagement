using FluentValidation;
using Svm.Services.Contracts.Catalog;

namespace Svm.Application.Catalog;

internal sealed class GetHardwareQueryValidator : AbstractValidator<GetHardwareQuery>
{
    public GetHardwareQueryValidator()
    {
        RuleFor(x => x.HardwareId).NotEmpty();
    }
}
internal sealed class ListHardwareQueryValidator : AbstractValidator<ListHardwareQuery>
{
    public ListHardwareQueryValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Input).Must(HardwareValidation.List);
    }
}
internal sealed class ListHardwareBindingsQueryValidator : AbstractValidator<ListHardwareBindingsQuery>
{
    public ListHardwareBindingsQueryValidator()
    {
        RuleFor(x => x.HardwareId).NotEmpty();
        RuleFor(x => x.Input).Must(HardwareValidation.List);
    }
}
internal sealed class GetWorkspaceDevicesQueryValidator : AbstractValidator<GetWorkspaceDevicesQuery>
{
    public GetWorkspaceDevicesQueryValidator()
    {
        RuleFor(x => x.Input).Must(HardwareValidation.List);
    }
}
internal sealed class GetWorkspaceInstallationsQueryValidator : AbstractValidator<GetWorkspaceInstallationsQuery>
{
    public GetWorkspaceInstallationsQueryValidator()
    {
        RuleFor(x => x.Input).Must(HardwareValidation.List);
    }
}
internal sealed class CreateHardwareCommandValidator : AbstractValidator<CreateHardwareCommand>
{
    public CreateHardwareCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Type).Must(x => x is "IPC" or "PLC");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.AssetNumber).MaximumLength(128);
        RuleFor(x => x.ManagementIp).Must(x => x is null || System.Net.IPAddress.TryParse(x, out _));
    }
}
internal sealed class UpdateHardwareCommandValidator : AbstractValidator<UpdateHardwareCommand>
{
    public UpdateHardwareCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.HardwareId).NotEmpty();
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.AssetNumber).MaximumLength(128);
        RuleFor(x => x.ManagementIp).Must(x => x is null || System.Net.IPAddress.TryParse(x, out _));
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class CreateHardwareBindingCommandValidator : AbstractValidator<CreateHardwareBindingCommand>
{
    public CreateHardwareBindingCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.HardwareId).NotEmpty();
        RuleFor(x => x.SoftwareId).Must(x => x != Guid.Empty);
        RuleFor(x => x).Must(x => (x.SoftwareId is null) != (x.NewSoftware is null));
        RuleFor(x => x.NewSoftware).Must(x => x is null || !string.IsNullOrWhiteSpace(x.Name) && x.Name.Length <= 128 && x.Category is "UpperComputer" or "Vision" && x.Description?.Length is not > 2000);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class RevokeHardwareBindingCommandValidator : AbstractValidator<RevokeHardwareBindingCommand>
{
    public RevokeHardwareBindingCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.HardwareId).NotEmpty();
        RuleFor(x => x.SoftwareId).NotEmpty();
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class BindInstanceHardwareCommandValidator : AbstractValidator<BindInstanceHardwareCommand>
{
    public BindInstanceHardwareCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.InstanceId).NotEmpty();
        RuleFor(x => x.HardwareBindingId).NotEmpty();
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.ExpectedBindingRevision).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal static class HardwareValidation
{
    internal static bool List(HardwareListInput? x) => x is not null && x.Filter is not null && x.PageSize is > 0 and <= 200 &&
        x.Filter.DeviceId != Guid.Empty && x.Filter.ProcessId != Guid.Empty && x.Filter.HardwareId != Guid.Empty && x.Filter.SoftwareId != Guid.Empty &&
        x.Filter.Name?.Length is not > 128 && x.Filter.Category is null or "UpperComputer" or "Vision" &&
        (x.After is null || x.After.Id != Guid.Empty && x.After.SortKey.Length is > 0 and <= 128);
}
