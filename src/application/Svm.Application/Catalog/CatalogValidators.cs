using FluentValidation;
using Svm.Services.Contracts.Catalog;

namespace Svm.Application.Catalog;

internal sealed class GetSiteQueryValidator : AbstractValidator<GetSiteQuery>
{
    public GetSiteQueryValidator()
    {

        RuleFor(x => x).NotNull();
    }
}
internal sealed class ListProcessesQueryValidator : AbstractValidator<ListProcessesQuery>
{
    public ListProcessesQueryValidator(SiteCatalogOptions options)
    {
        RuleFor(x => x.Input).Must(x => CatalogValidation.Valid(x, options.MaximumPageSize));
    }
}
internal sealed class GetProcessQueryValidator : AbstractValidator<GetProcessQuery>
{
    public GetProcessQueryValidator()
    {
        RuleFor(x => x.ProcessId).NotEmpty();
    }
}
internal sealed class ListDevicesQueryValidator : AbstractValidator<ListDevicesQuery>
{
    public ListDevicesQueryValidator(SiteCatalogOptions options)
    {
        RuleFor(x => x.Input).Must(x => CatalogValidation.Valid(x, options.MaximumPageSize));
    }
}
internal sealed class GetDeviceQueryValidator : AbstractValidator<GetDeviceQuery>
{
    public GetDeviceQueryValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
    }
}
internal sealed class ListBindingsQueryValidator : AbstractValidator<ListBindingsQuery>
{
    public ListBindingsQueryValidator(SiteCatalogOptions options)
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Input).Must(x => CatalogValidation.Valid(x, options.MaximumPageSize));
    }
}
internal sealed class GetDeviceInventoryQueryValidator : AbstractValidator<GetDeviceInventoryQuery>
{
    public GetDeviceInventoryQueryValidator(SiteCatalogOptions options)
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Input).Must(x => CatalogValidation.Valid(x, options.MaximumPageSize));
    }
}
internal sealed class ListSoftwareQueryValidator : AbstractValidator<ListSoftwareQuery>
{
    public ListSoftwareQueryValidator(SiteCatalogOptions options)
    {
        RuleFor(x => x.Input).Must(x => CatalogValidation.Valid(x, options.MaximumPageSize));
    }
}
internal sealed class GetSoftwareQueryValidator : AbstractValidator<GetSoftwareQuery>
{
    public GetSoftwareQueryValidator()
    {
        RuleFor(x => x.SoftwareId).NotEmpty();
    }
}
internal sealed class GetPermissionOptionsQueryValidator : AbstractValidator<GetPermissionOptionsQuery>
{
    public GetPermissionOptionsQueryValidator(SiteCatalogOptions options)
    {
        RuleFor(x => x.Input).Must(x => CatalogValidation.Valid(x, options.MaximumPageSize));
    }
}
internal sealed class CreateSoftwareCommandValidator : AbstractValidator<CreateSoftwareCommand>
{
    public CreateSoftwareCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(64).Must(x => x is not null && !x.Any(char.IsControl) && x.Trim() == x);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Category).Must(x => x is "UpperComputer" or "Vision");
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}
internal sealed class UpdateSoftwareCommandValidator : AbstractValidator<UpdateSoftwareCommand>
{
    public UpdateSoftwareCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.SoftwareId).NotEmpty();
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class CreateProcessCommandValidator : AbstractValidator<CreateProcessCommand>
{
    public CreateProcessCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(64).Must(x => x is not null && !x.Any(char.IsControl) && x.Trim() == x);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
    }
}
internal sealed class UpdateProcessCommandValidator : AbstractValidator<UpdateProcessCommand>
{
    public UpdateProcessCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.ProcessId).NotEmpty();
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class CreateDeviceCommandValidator : AbstractValidator<CreateDeviceCommand>
{
    public CreateDeviceCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.ProcessId).NotEmpty();
        RuleFor(x => x.DeviceNo).NotEmpty().MaximumLength(64).Must(x => x is not null && !x.Any(char.IsControl) && x.Trim() == x);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
    }
}
internal sealed class UpdateDeviceCommandValidator : AbstractValidator<UpdateDeviceCommand>
{
    public UpdateDeviceCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.ProcessId).Must(x => x is null || x != Guid.Empty);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128).When(x => x.Name is not null);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
        RuleFor(x => x).Must(x => x.ProcessId is not null || x.Name is not null);
    }
}
internal sealed class CreateBindingCommandValidator : AbstractValidator<CreateBindingCommand>
{
    public CreateBindingCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.SoftwareId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class RevokeBindingCommandValidator : AbstractValidator<RevokeBindingCommand>
{
    public RevokeBindingCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.SoftwareId).NotEmpty();
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal static class CatalogValidation
{
    internal static bool Valid(CatalogListInput? input, int maximum) => input is not null && input.Filter is not null &&
        input.PageSize > 0 && input.PageSize <= maximum && input.Filter.Code?.Length is not > 64 && input.Filter.Name?.Length is not > 128 &&
        (input.Filter.Category is null or "UpperComputer" or "Vision") && input.Filter.ProcessId != Guid.Empty && input.Filter.SoftwareId != Guid.Empty &&
        (input.After is null || input.After.Id != Guid.Empty && input.After.SortKey.Length is > 0 and <= 64);
}
