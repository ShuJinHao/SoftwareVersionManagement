using FluentValidation;
using Svm.Services.Contracts.Packages;

namespace Svm.Application.Packages;

internal sealed class RequestPackageCleanupValidator : PackageValidator<RequestPackageCleanupCommand>
{ public RequestPackageCleanupValidator() => RuleFor(x => x).Must(x => x.Key != Guid.Empty && x.PackageId != Guid.Empty && x.ExpectedRevision > 0 && Text(x.Reason, 256)); }
internal sealed class PackageCleanupStatusValidator : PackageValidator<GetPackageCleanupStatusQuery>
{ public PackageCleanupStatusValidator() => RuleFor(x => x.PackageId).NotEmpty(); }
internal sealed class RecordDownloadStopValidator : PackageValidator<RecordDownloadStopCommand>
{
    public RecordDownloadStopValidator() => RuleFor(x => x).Must(x => x.Key != Guid.Empty && x.PackageId != Guid.Empty && x.Input is not null &&
        Text(x.Input.NodeId,64) && x.Input.WorkerGeneration != Guid.Empty && x.Input.StoppedAt != default && x.Input.ProcessStoppedConfirmed &&
        Text(x.Input.Evidence,2000) && Text(x.Input.Reason,256) && x.Input.ExpectedRevision > 0);
}
internal sealed class RecordCleanupReplicaValidator : PackageValidator<RecordCleanupReplicaCommand>
{ public RecordCleanupReplicaValidator() => RuleFor(x => x).Must(x => Lease(x.Lease) && Text(x.NodeId,64)); }
internal sealed class CompletePackageCleanupValidator : PackageValidator<CompletePackageCleanupCommand>
{ public CompletePackageCleanupValidator() => RuleFor(x => x).Must(x => Lease(x.Lease)); }
