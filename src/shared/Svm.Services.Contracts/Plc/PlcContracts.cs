using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
namespace Svm.Services.Contracts.Plc;

public sealed record PlcScope(Guid BindingId, long BindingRevision);
public sealed record PlcCredentialView(Guid Id, Guid SoftwareId, string Name, IReadOnlyList<PlcScope> Scope,
    DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, Guid CreatedBy, DateTimeOffset CreatedAt, long Revision);
public sealed record PlcUsageView(Guid Id, Guid HardwareId, Guid BindingId, long BindingRevision, Guid SoftwareId,
    Guid ReleaseId, string Version, string ReleaseState, Guid? PreviousReleaseId, DateTimeOffset FieldAt,
    DateTimeOffset RecordedAt, Guid ActorId, string EmployeeNo, string Note, string Materials, Guid? CorrectsId);
public sealed record PlcUsageSummary(long Revision, PlcUsageView? Current);
public sealed record PlcEvidenceView(Guid Id, Guid ReleaseId, Guid HardwareId, Guid BindingId, long BindingRevision,
    DateTimeOffset FieldAt, string Verification, string Materials, Guid ActorId, string EmployeeNo, DateTimeOffset RecordedAt);
public sealed record PlcPage<T>(IReadOnlyList<T> Items, Guid? Next);
public sealed record PlcOptions(int? UploadCredentialMaxLifetimeSeconds = null)
{
    public void Check(DateTimeOffset now, DateTimeOffset expiry)
    {
        if (UploadCredentialMaxLifetimeSeconds is null or <= 0) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        if (expiry <= now || expiry > now.AddSeconds(UploadCredentialMaxLifetimeSeconds.Value)) throw new RequestRejectedException(RequestFailure.ValidationFailed);
    }
}
public interface IPlcUploadProvenance
{ Task<bool> IsAcceptedUploaderAsync(Guid subject, Guid softwareId, CancellationToken token); }
public interface IPlcUploadAccess
{
    Task<PlcCredentialView?> AuthenticateAsync(AccessProof proof, bool protect, CancellationToken token);
    Task<PlcCredentialView> GetAsync(Guid id, bool protect, CancellationToken token);
    Task<PlcCredentialView> CreateAsync(CreatePlcCredentialCommand input, Guid actor, CancellationToken token);
    Task<PlcCredentialView> RevokeAsync(Guid id, long revision, CancellationToken token);
    Task<bool> IsAcceptedUploaderAsync(Guid subject, Guid softwareId, CancellationToken token);
}
public interface IPlcUsage
{
    Task<PlcUsageSummary> StateAsync(Guid bindingId, long bindingRevision, bool protect, CancellationToken token);
    Task<PlcUsageView> RecordAsync(RecordPlcUsageCommand input, Guid hardwareId, Guid softwareId, string version, string releaseState,
        Guid actor, string employeeNo, CancellationToken token);
    Task<PlcUsageView> GetAsync(Guid id, CancellationToken token);
}
public interface IPlcPublication
{
    Task BindUploadAsync(Guid releaseId, PlcScope scope, CancellationToken token);
    Task<PlcScope?> UploadScopeAsync(Guid releaseId, CancellationToken token);
    Task<ReleaseView> PublishAsync(PublishPlcReleaseCommand input, Guid hardwareId, Guid actor, string employeeNo, CancellationToken token);
    Task<PlcEvidenceView?> GetAsync(Guid releaseId, CancellationToken token);
}
public interface IPlcQueries
{
    Task<PlcPage<PlcCredentialView>> CredentialsAsync(Guid softwareId, int size, Guid? after, CancellationToken token);
    Task<PlcPage<PlcUsageView>> UsageAsync(Guid hardwareId, int size, Guid? after, CancellationToken token);
    Task<IReadOnlyDictionary<Guid, PlcUsageSummary>> CurrentAsync(IReadOnlyList<Guid> bindings, CancellationToken token);
}
