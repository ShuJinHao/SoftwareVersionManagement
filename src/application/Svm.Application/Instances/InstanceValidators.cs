using FluentValidation;
using Svm.Services.Contracts.Instances;

namespace Svm.Application.Instances;

internal sealed class CreateEnrollmentGrantCommandValidator : AbstractValidator<CreateEnrollmentGrantCommand>
{
    public CreateEnrollmentGrantCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.SoftwareId).NotEmpty();
        RuleFor(x=>x.DeviceIds).Must(v=>v is { Count: >0 and <=200 } && v.All(id=>id!=Guid.Empty) && v.Distinct().Count()==v.Count);
        RuleFor(x=>x.ExpiresAt).NotEmpty();
        RuleFor(x=>x.MaxInstances).InclusiveBetween(1,100000);
        RuleFor(x=>x.SecretMaterial).Must(InstanceValidation.Secret);
        RuleFor(x=>x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class CreateRecoveryGrantCommandValidator : AbstractValidator<CreateRecoveryGrantCommand>
{
    public CreateRecoveryGrantCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.InstanceId).NotEmpty();
        RuleFor(x=>x.ExpiresAt).NotEmpty();
        RuleFor(x=>x.SecretMaterial).Must(InstanceValidation.Secret);
        RuleFor(x=>x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class RegisterInstanceCommandValidator : AbstractValidator<RegisterInstanceCommand>
{
    public RegisterInstanceCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.SoftwareId).NotEmpty();
        RuleFor(x=>x.InstallationKey).NotEmpty();
        RuleFor(x=>x.DeviceId).NotEmpty();
        RuleFor(x=>x.SecretMaterial).Must(InstanceValidation.Secret);
    }
}
internal sealed class RecoverInstanceCommandValidator : AbstractValidator<RecoverInstanceCommand>
{
    public RecoverInstanceCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.SecretMaterial).Must(InstanceValidation.Secret);
    }
}
internal sealed class OpenReportStreamCommandValidator : AbstractValidator<OpenReportStreamCommand>
{
    public OpenReportStreamCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.ExpectedEpoch).GreaterThanOrEqualTo(0).LessThan(long.MaxValue);
    }
}
internal sealed class SubmitStatusReportCommandValidator : AbstractValidator<SubmitStatusReportCommand>
{
    public SubmitStatusReportCommandValidator()
    {
        RuleFor(x=>x.Report).Must(InstanceValidation.Report);
    }
}
internal sealed class GetClientContextQueryValidator : AbstractValidator<GetClientContextQuery>
{
    public GetClientContextQueryValidator()
    {
        RuleFor(x=>x).NotNull();
    }
}
internal sealed class ListEnrollmentGrantsQueryValidator : AbstractValidator<ListEnrollmentGrantsQuery>
{
    public ListEnrollmentGrantsQueryValidator()
    {
        RuleFor(x=>x.SoftwareId).NotEmpty();
        RuleFor(x=>x.PageSize).InclusiveBetween(1,200);
        RuleFor(x=>x.After).Must(id=>id!=Guid.Empty);
    }
}
internal sealed class GetInstanceHistoryQueryValidator : AbstractValidator<GetInstanceHistoryQuery>
{
    public GetInstanceHistoryQueryValidator()
    {
        RuleFor(x=>x.InstanceId).NotEmpty();
        RuleFor(x=>x.PageSize).InclusiveBetween(1,200);
        RuleFor(x=>x.After).Must(id=>id!=Guid.Empty);
    }
}
internal sealed class ListInstanceCredentialsQueryValidator : AbstractValidator<ListInstanceCredentialsQuery>
{
    public ListInstanceCredentialsQueryValidator()
    {
        RuleFor(x=>x.InstanceId).NotEmpty();
        RuleFor(x=>x.PageSize).InclusiveBetween(1,200);
        RuleFor(x=>x.After).Must(id=>id!=Guid.Empty);
    }
}
internal sealed class GetInstanceQueryValidator : AbstractValidator<GetInstanceQuery>
{
    public GetInstanceQueryValidator()
    {
        RuleFor(x=>x.InstanceId).NotEmpty();
    }
}
internal sealed class ListInstancesQueryValidator : AbstractValidator<ListInstancesQuery>
{
    public ListInstancesQueryValidator()
    {
        RuleFor(x=>x.Input).Must(x=>x is not null && x.PageSize is >0 and <=200 && x.After!=Guid.Empty && x.Filter is not null && x.Filter.SoftwareId!=Guid.Empty && x.Filter.ProcessId!=Guid.Empty && x.Filter.DeviceId!=Guid.Empty && x.Filter.InstalledReleaseId!=Guid.Empty && (x.Filter.DeviceNo is null || x.Filter.DeviceNo.Length<=64) && (x.Filter.ReportedIp is null || System.Net.IPAddress.TryParse(x.Filter.ReportedIp,out _)) && (x.Filter.Freshness is null or "NeverReported" or "Fresh" or "Unknown") && (x.Filter.RunningState is null or "Running" or "Stopped" or "Unknown") && (x.Filter.Lifecycle is null or "Active" or "Suspended"));
    }
}
internal sealed class RevokeEnrollmentGrantCommandValidator : AbstractValidator<RevokeEnrollmentGrantCommand>
{
    public RevokeEnrollmentGrantCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.GrantId).NotEmpty();
        RuleFor(x=>x.ExpectedRevision).GreaterThan(0);
        RuleFor(x=>x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class RevokeRecoveryGrantCommandValidator : AbstractValidator<RevokeRecoveryGrantCommand>
{
    public RevokeRecoveryGrantCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.GrantId).NotEmpty();
        RuleFor(x=>x.ExpectedRevision).GreaterThan(0);
        RuleFor(x=>x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class RevokeInstanceCredentialCommandValidator : AbstractValidator<RevokeInstanceCredentialCommand>
{
    public RevokeInstanceCredentialCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.CredentialId).NotEmpty();
        RuleFor(x=>x.ExpectedRevision).GreaterThan(0);
        RuleFor(x=>x.Reason).NotEmpty().MaximumLength(256);
    }
}
internal sealed class UpdateInstanceLifecycleCommandValidator : AbstractValidator<UpdateInstanceLifecycleCommand>
{
    public UpdateInstanceLifecycleCommandValidator()
    {
        RuleFor(x=>x.Key).NotEmpty();
        RuleFor(x=>x.InstanceId).NotEmpty();
        RuleFor(x=>x.ExpectedRevision).GreaterThan(0);
        RuleFor(x=>x.Reason).NotEmpty().MaximumLength(256);
        RuleFor(x=>x.Lifecycle).Must(v=>v is "Active" or "Suspended");
    }
}
