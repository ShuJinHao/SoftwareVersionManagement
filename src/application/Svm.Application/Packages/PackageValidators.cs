using FluentValidation;
using Svm.Services.Contracts.Packages;

namespace Svm.Application.Packages;

internal abstract class PackageValidator<T> : AbstractValidator<T>
{
    protected static bool Text(string? x,int max) => !string.IsNullOrWhiteSpace(x) && x.Length<=max && !x.Any(char.IsControl);
    protected static bool Digest(string? x) => x is {Length:64} && x.All(char.IsAsciiHexDigit);
    protected static bool Version(string x) => x.Split('.') is {Length:3} parts && parts.All(p=>int.TryParse(p,out var n)&&n>=0&&p==n.ToString(System.Globalization.CultureInfo.InvariantCulture));
    protected static bool Size(int x) => x is >=1 and <=200;
    protected static bool List(ReleaseListInput? x) => x is not null && x.SoftwareId!=Guid.Empty && Size(x.PageSize) && x.State is null or "Staging" or "Test" or "Formal" or "Disabled" && x.Channel is null or "Test" or "Formal";
    protected static bool Lease(PackageLease? x) => x is not null && x.Work is not null && x.Work.WorkId!=Guid.Empty && x.LeaseToken!=Guid.Empty && x.LeaseGeneration>0;
    protected static bool Facts(IReadOnlyList<ReplicaFact>? x) => x is {Count:2} && x.All(f=>f is not null && Text(f.NodeId,64) && f.State is "Healthy" or "Missing" or "Suspect");
}

internal sealed class CreateReleaseCommandValidator : PackageValidator<CreateReleaseCommand>
{
    public CreateReleaseCommandValidator() => RuleFor(x=>x).Must(x=>x.Key != Guid.Empty && x.SoftwareId != Guid.Empty && x.ChangeLevel is "Patch" or "Minor" or "Major" && Text(x.ChangeSummary,2000) && Text(x.ChangeReason,256) && x.Package is not null && Text(x.Package.FileName,255) && x.Package.SizeBytes>0 && Digest(x.Package.Sha256) && (x.ExpectedVersion is null || Version(x.ExpectedVersion))).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class DisableReleaseCommandValidator : PackageValidator<DisableReleaseCommand>
{
    public DisableReleaseCommandValidator() => RuleFor(x=>x).Must(x=>x.Key!=Guid.Empty && x.ReleaseId!=Guid.Empty && x.ExpectedRevision>0 && Text(x.Reason,256)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class PublishReleaseCommandValidator : PackageValidator<PublishReleaseCommand>
{
    public PublishReleaseCommandValidator() => RuleFor(x => x).Must(x => x.Key != Guid.Empty && x.ReleaseId != Guid.Empty &&
        x.ExpectedRevision > 0 && x.TestEvidenceId != Guid.Empty && Text(x.PublishReason, 256) && !string.IsNullOrWhiteSpace(x.PublishConclusion) && x.PublishConclusion.Length <= 2000 &&
        !x.PublishConclusion.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))).WithErrorCode("VALIDATION_FAILED");
}
internal sealed class RetryPackageCommandValidator : PackageValidator<RetryPackageCommand>
{
    public RetryPackageCommandValidator() => RuleFor(x=>x).Must(x=>x.Key!=Guid.Empty && x.PackageId!=Guid.Empty && x.ExpectedRevision>0 && Text(x.Reason,256)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class UploadContentCommandValidator : PackageValidator<UploadContentCommand>
{
    public UploadContentCommandValidator() => RuleFor(x=>x).Must(x=>x.UploadId!=Guid.Empty && x.Content is {CanRead:true} && (x.ContentLength is null || x.ContentLength>0)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class BeginUploadCommandValidator : PackageValidator<BeginUploadCommand>
{
    public BeginUploadCommandValidator() => RuleFor(x=>x).Must(x=>x.UploadId!=Guid.Empty && x.ReceiveToken!=Guid.Empty && Text(x.NodeId,64)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class FinishUploadCommandValidator : PackageValidator<FinishUploadCommand>
{
    public FinishUploadCommandValidator() => RuleFor(x=>x).Must(x=>x.Receipt is not null && x.Receipt.UploadId!=Guid.Empty && x.Receipt.ReceiveToken!=Guid.Empty && x.Size>0 && Digest(x.Sha256)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class FailUploadCommandValidator : PackageValidator<FailUploadCommand>
{
    public FailUploadCommandValidator() => RuleFor(x=>x).Must(x=>x.UploadId!=Guid.Empty && x.ReceiveToken!=Guid.Empty && Text(x.Code,64)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class CheckUploadQueryValidator : PackageValidator<CheckUploadQuery>
{
    public CheckUploadQueryValidator() => RuleFor(x=>x).Must(x=>x.UploadId!=Guid.Empty && x.ReceiveToken!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class ListReleasesQueryValidator : PackageValidator<ListReleasesQuery>
{
    public ListReleasesQueryValidator() => RuleFor(x=>x).Must(x=>List(x.Input)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class GetReleaseQueryValidator : PackageValidator<GetReleaseQuery>
{
    public GetReleaseQueryValidator() => RuleFor(x=>x).Must(x=>x.ReleaseId!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class GetPackageQueryValidator : PackageValidator<GetPackageQuery>
{
    public GetPackageQueryValidator() => RuleFor(x=>x).Must(x=>x.PackageId!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class GetPackageWorkQueryValidator : PackageValidator<GetPackageWorkQuery>
{
    public GetPackageWorkQueryValidator() => RuleFor(x=>x).Must(x=>x.WorkId!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class GetTestEvidenceQueryValidator : PackageValidator<GetTestEvidenceQuery>
{
    public GetTestEvidenceQueryValidator() => RuleFor(x=>x).Must(x=>x.ReleaseId!=Guid.Empty && Size(x.PageSize) && x.After!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class GetDownloadAuditQueryValidator : PackageValidator<GetDownloadAuditQuery>
{
    public GetDownloadAuditQueryValidator() => RuleFor(x=>x).Must(x=>x.SoftwareId!=Guid.Empty && Size(x.PageSize) && x.After!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class ListClientReleasesQueryValidator : PackageValidator<ListClientReleasesQuery>
{
    public ListClientReleasesQueryValidator() => RuleFor(x=>x).Must(x=>List(x.Input) && x.Input.Channel is null or "Formal" or "Test").WithErrorCode("VALIDATION_FAILED");
}

internal sealed class GetClientReleaseQueryValidator : PackageValidator<GetClientReleaseQuery>
{
    public GetClientReleaseQueryValidator() => RuleFor(x=>x).Must(x=>x.ReleaseId!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class GetClientPackageQueryValidator : PackageValidator<GetClientPackageQuery>
{
    public GetClientPackageQueryValidator() => RuleFor(x=>x).Must(x=>x.PackageId!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class ClaimPackageWorkCommandValidator : PackageValidator<ClaimPackageWorkCommand>
{
    public ClaimPackageWorkCommandValidator() => RuleFor(x=>x).Must(x=>x.WorkId!=Guid.Empty && x.LeaseToken!=Guid.Empty && Text(x.NodeId,64)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class RenewPackageWorkCommandValidator : PackageValidator<RenewPackageWorkCommand>
{
    public RenewPackageWorkCommandValidator() => RuleFor(x=>x).Must(x=>Lease(x.Lease)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class CompletePackageWorkCommandValidator : PackageValidator<CompletePackageWorkCommand>
{
    public CompletePackageWorkCommandValidator() => RuleFor(x=>x).Must(x=>Lease(x.Lease) && Facts(x.Facts)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class FailPackageWorkCommandValidator : PackageValidator<FailPackageWorkCommand>
{
    public FailPackageWorkCommandValidator() => RuleFor(x=>x).Must(x=>Lease(x.Lease) && Text(x.Code,64)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class CheckPackageReplicasCommandValidator : PackageValidator<CheckPackageReplicasCommand>
{
    public CheckPackageReplicasCommandValidator() => RuleFor(x=>x).Must(x=>x.PackageId!=Guid.Empty && Facts(x.Facts)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class GetPackageAuthorityQueryValidator : PackageValidator<GetPackageAuthorityQuery>
{
    public GetPackageAuthorityQueryValidator() => RuleFor(x=>x).Must(x=>x.WorkId!=Guid.Empty).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class AuthorizeDownloadCommandValidator : PackageValidator<AuthorizeDownloadCommand>
{
    public AuthorizeDownloadCommandValidator() => RuleFor(x=>x).Must(x=>x.PackageId!=Guid.Empty && x.RequestId!=Guid.Empty && x.WorkerGeneration!=Guid.Empty && Text(x.NodeId,64)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class RecordDownloadEndCommandValidator : PackageValidator<RecordDownloadEndCommand>
{
    public RecordDownloadEndCommandValidator() => RuleFor(x=>x).Must(x=>x.End is not null && x.End.RequestId!=Guid.Empty && x.End.WorkerGeneration!=Guid.Empty && x.End.BytesSent>=0 && x.End.Outcome is "Closed" or "Interrupted" && Text(x.End.NodeId,64)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class InspectReplicaQueryValidator : AbstractValidator<InspectReplicaQuery>
{ public InspectReplicaQueryValidator() => RuleFor(x=>x.PackageId).NotEmpty(); }

internal sealed class GetPackageCapabilitiesQueryValidator : AbstractValidator<GetPackageCapabilitiesQuery>
{ public GetPackageCapabilitiesQueryValidator() => RuleFor(x=>x).NotNull(); }

internal sealed class GetUploadTargetQueryValidator : AbstractValidator<GetUploadTargetQuery>
{ public GetUploadTargetQueryValidator() => RuleFor(x=>x.UploadId).NotEmpty(); }

internal sealed class GetDownloadEndQueryValidator : PackageValidator<GetDownloadEndQuery>
{ public GetDownloadEndQueryValidator() => RuleFor(x=>x).Must(x=>x.RequestId!=Guid.Empty && x.WorkerGeneration!=Guid.Empty && Text(x.NodeId,64)); }
