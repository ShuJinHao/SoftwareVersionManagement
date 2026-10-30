using MediatR;
using FluentValidation;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Plc;
using Svm.Application.Packages;
namespace Svm.Application.Plc;

internal sealed class PlcUploadContentCommandValidator : PackageValidator<PlcUploadContentCommand>
{
    public PlcUploadContentCommandValidator() => RuleFor(x=>x).Must(x=>x.UploadId!=Guid.Empty && x.Content is {CanRead:true} && (x.ContentLength is null || x.ContentLength>0)).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class PlcBeginUploadCommandValidator : PackageValidator<PlcBeginUploadCommand>
{
    public PlcBeginUploadCommandValidator() => RuleFor(x=>x).Must(x=>x.UploadId!=Guid.Empty && x.ReceiveToken!=Guid.Empty && Text(x.NodeId,64)).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class PlcFinishUploadCommandValidator : PackageValidator<PlcFinishUploadCommand>
{
    public PlcFinishUploadCommandValidator() => RuleFor(x=>x).Must(x=>x.Receipt is not null && x.Receipt.UploadId!=Guid.Empty && x.Receipt.ReceiveToken!=Guid.Empty && x.Size>0 && Digest(x.Sha256)).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class PlcFailUploadCommandValidator : PackageValidator<PlcFailUploadCommand>
{
    public PlcFailUploadCommandValidator() => RuleFor(x=>x).Must(x=>x.UploadId!=Guid.Empty && x.ReceiveToken!=Guid.Empty && Text(x.Code,64)).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class PlcCheckUploadQueryValidator : PackageValidator<PlcCheckUploadQuery>
{
    public PlcCheckUploadQueryValidator() => RuleFor(x=>x).Must(x=>x.UploadId!=Guid.Empty && x.ReceiveToken!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class PlcRetryPackageCommandValidator : PackageValidator<PlcRetryPackageCommand>
{
    public PlcRetryPackageCommandValidator() => RuleFor(x=>x).Must(x=>x.Key!=Guid.Empty && x.PackageId!=Guid.Empty && x.ExpectedRevision>0 && Text(x.Reason,256)).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class PlcGetPackageQueryValidator : PackageValidator<PlcGetPackageQuery>
{
    public PlcGetPackageQueryValidator() => RuleFor(x=>x).Must(x=>x.PackageId!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class PlcGetPackageWorkQueryValidator : PackageValidator<PlcGetPackageWorkQuery>
{
    public PlcGetPackageWorkQueryValidator() => RuleFor(x=>x).Must(x=>x.WorkId!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class PlcGetUploadTargetQueryValidator : AbstractValidator<PlcGetUploadTargetQuery>
{ public PlcGetUploadTargetQueryValidator() => RuleFor(x=>x.UploadId).NotEmpty(); }