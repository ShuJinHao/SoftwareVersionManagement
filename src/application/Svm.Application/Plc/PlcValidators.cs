using FluentValidation;
using Svm.Application.Packages;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Plc;
namespace Svm.Application.Plc;

internal sealed class CreatePlcCredentialValidator : PackageValidator<CreatePlcCredentialCommand>
{
    public CreatePlcCredentialValidator() => RuleFor(x=>x).Must(x=>x.Key!=Guid.Empty && x.SoftwareId!=Guid.Empty && Text(x.Name,128) &&
        x.ExpiresAt!=default && InstanceValidation.Secret(x.SecretMaterial) && x.Scope is { Count: >0 and <=200 } &&
        x.Scope.All(p=>p is not null && p.BindingId!=Guid.Empty && p.BindingRevision>0) && x.Scope.Select(p=>p.BindingId).Distinct().Count()==x.Scope.Count);
}
internal sealed class RevokePlcCredentialValidator : PackageValidator<RevokePlcCredentialCommand>
{ public RevokePlcCredentialValidator()=>RuleFor(x=>x).Must(x=>x.Key!=Guid.Empty && x.CredentialId!=Guid.Empty && x.ExpectedRevision>0 && Text(x.Reason,256)); }
internal sealed class PublishPlcReleaseValidator : PackageValidator<PublishPlcReleaseCommand>
{
    public PublishPlcReleaseValidator(TimeProvider clock)=>RuleFor(x=>x).Must(x=>x.Key!=Guid.Empty && x.ReleaseId!=Guid.Empty && x.BindingId!=Guid.Empty && x.BindingRevision>0 && x.ExpectedRevision>0 &&
        x.FieldAt!=default && x.FieldAt<=clock.GetUtcNow() && Text(x.Verification,2000) && Text(x.Materials,2000) && Text(x.PublishReason,256) && Text(x.PublishConclusion,2000));
}
internal sealed class RecordPlcUsageValidator : PackageValidator<RecordPlcUsageCommand>
{
    public RecordPlcUsageValidator(TimeProvider clock)=>RuleFor(x=>x).Must(x=>x.Key!=Guid.Empty && x.ReleaseId!=Guid.Empty && x.BindingId!=Guid.Empty && x.BindingRevision>0 && x.ExpectedRevision>=0 &&
        x.FieldAt!=default && x.FieldAt<=clock.GetUtcNow() && Text(x.Note,2000) && Text(x.Materials,2000) && x.CorrectsId!=Guid.Empty);
}
internal sealed class ListPlcCredentialsValidator : AbstractValidator<ListPlcCredentialsQuery>
{ public ListPlcCredentialsValidator()=>RuleFor(x=>x).Must(x=>x.SoftwareId!=Guid.Empty && x.PageSize is >0 and <=200 && x.After!=Guid.Empty); }
internal sealed class ListPlcUsageValidator : AbstractValidator<ListPlcUsageQuery>
{ public ListPlcUsageValidator()=>RuleFor(x=>x).Must(x=>x.HardwareId!=Guid.Empty && x.PageSize is >0 and <=200 && x.After!=Guid.Empty); }
internal sealed class GetPlcUsageValidator : AbstractValidator<GetPlcUsageQuery>
{ public GetPlcUsageValidator()=>RuleFor(x=>x.BindingId).NotEmpty(); }
internal sealed class GetPlcEvidenceValidator : AbstractValidator<GetPlcEvidenceQuery>
{ public GetPlcEvidenceValidator()=>RuleFor(x=>x.ReleaseId).NotEmpty(); }
internal sealed class CreatePlcArchiveValidator : PackageValidator<CreatePlcArchiveCommand>
{
    public CreatePlcArchiveValidator()=>RuleFor(x=>x).Must(x=>x.Key!=Guid.Empty && x.BindingId!=Guid.Empty && x.BindingRevision>0 &&
        x.ChangeLevel is "Patch" or "Minor" or "Major" && Text(x.ChangeSummary,2000) && Text(x.ChangeReason,256) &&
        (x.ExpectedVersion is null || Text(x.ExpectedVersion,128)) && x.Package is {} p && Text(p.FileName,255) && p.SizeBytes>0 && Digest(p.Sha256));
}
