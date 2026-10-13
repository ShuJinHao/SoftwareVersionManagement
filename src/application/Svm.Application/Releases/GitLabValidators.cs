using FluentValidation;
using Svm.Services.Contracts.Releases;
namespace Svm.Application.Releases;

internal abstract class GitLabValidator<T> : AbstractValidator<T>
{
    protected static bool Lease(GitLabLease? lease) => lease is not null && lease.WorkId != Guid.Empty && lease.Token != Guid.Empty && lease.Generation > 0;
    protected static bool Fact(GitLabReleaseFacts? facts) => facts is not null && facts.Manifest is not null && facts.Manifest.IsValid() && facts.ReleaseName is { Length: > 0 and <= 2000 } && facts.PipelineUrl is { Length: > 0 and <= 2000 } && facts.ReleasedAt.Offset == TimeSpan.Zero;
}

internal sealed class RequestReleaseSyncCommandValidator : GitLabValidator<RequestReleaseSyncCommand>
{ public RequestReleaseSyncCommandValidator() => RuleFor(x => x).Must(x => x.Key != Guid.Empty && x.SoftwareId != Guid.Empty && (x.Tag is null || GitLabVersions.TryParse(x.Tag, out _))).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class GetReleaseSourceQueryValidator : GitLabValidator<GetReleaseSourceQuery>
{ public GetReleaseSourceQueryValidator() => RuleFor(x => x).Must(x => x.SoftwareId != Guid.Empty).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class GetReleaseSyncWorkQueryValidator : GitLabValidator<GetReleaseSyncWorkQuery>
{ public GetReleaseSyncWorkQueryValidator() => RuleFor(x => x).Must(x => x.WorkId != Guid.Empty).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class ScheduleGitLabScanCommandValidator : GitLabValidator<ScheduleGitLabScanCommand>
{ public ScheduleGitLabScanCommandValidator() => RuleFor(x => x).Must(x => x.SoftwareId != Guid.Empty).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class ClaimGitLabWorkCommandValidator : GitLabValidator<ClaimGitLabWorkCommand>
{ public ClaimGitLabWorkCommandValidator() => RuleFor(x => x).Must(x => x.WorkId != Guid.Empty && x.LeaseToken != Guid.Empty).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class ApplyGitLabScanCommandValidator : GitLabValidator<ApplyGitLabScanCommand>
{
    public ApplyGitLabScanCommandValidator() => RuleFor(x => x).Must(x =>
        Lease(x.Lease) && x.Releases is { Count: <= 100 } && x.Releases.All(Fact) &&
        x.Tags is { Count: <= 100 } && x.Tags.All(t => GitLabVersions.TryParse(t, out _)) &&
        x.PendingTags is { Count: <= 100 } && x.PendingTags.Distinct(StringComparer.Ordinal).Count() == x.PendingTags.Count &&
        x.PendingTags.All(tag => x.Tags.Contains(tag, StringComparer.Ordinal) && !x.Releases.Any(r => r.Manifest.Tag == tag)) &&
        (x.NextPage is null || x.NextPage is >= 2 and <= 10000)).WithErrorCode("VALIDATION_FAILED");
}

internal sealed class BeginGitLabImportCommandValidator : GitLabValidator<BeginGitLabImportCommand>
{ public BeginGitLabImportCommandValidator() => RuleFor(x => x).Must(x => Lease(x.Lease) && Fact(x.Facts) && x.ReceiveToken != Guid.Empty && x.NodeId is { Length: > 0 and <= 64 }).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class FinishGitLabImportCommandValidator : GitLabValidator<FinishGitLabImportCommand>
{ public FinishGitLabImportCommandValidator() => RuleFor(x => x).Must(x => Lease(x.Lease) && x.Receipt is not null && x.Receipt.ReceiveToken != Guid.Empty && x.Size > 0 && x.Sha256 is { Length: 64 } && x.Sha256.All(char.IsAsciiHexDigit)).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class RenewGitLabWorkCommandValidator : GitLabValidator<RenewGitLabWorkCommand>
{ public RenewGitLabWorkCommandValidator() => RuleFor(x => x).Must(x => Lease(x.Lease)).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class DelayGitLabWorkCommandValidator : GitLabValidator<DelayGitLabWorkCommand>
{ public DelayGitLabWorkCommandValidator() => RuleFor(x => x).Must(x => Lease(x.Lease) && x.Code is "VERSION_CONFLICT" or "VALIDATION_FAILED" or "CREDENTIAL_INVALID" or "RESOURCE_NOT_FOUND" or "DEPENDENCY_UNAVAILABLE" or "CHECKSUM_MISMATCH" or "PIPELINE_PENDING" or "IMPORT_INTERRUPTED" or "INVALID_STATE" or "PAYLOAD_TOO_LARGE").WithErrorCode("VALIDATION_FAILED"); }

internal sealed class GetGitLabWorkQueryValidator : GitLabValidator<GetGitLabWorkQuery>
{ public GetGitLabWorkQueryValidator() => RuleFor(x => x).Must(x => x.WorkId != Guid.Empty).WithErrorCode("VALIDATION_FAILED"); }

internal sealed class GetGitLabSourceQueryValidator : GitLabValidator<GetGitLabSourceQuery>
{ public GetGitLabSourceQueryValidator() => RuleFor(x => x).Must(x => x.SoftwareId != Guid.Empty).WithErrorCode("VALIDATION_FAILED"); }
