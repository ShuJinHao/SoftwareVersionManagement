using System.Net;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Catalog;

namespace Svm.Services.Contracts.Instances;

public sealed record InstanceAccessOptions(int? EnrollmentMaxLifetimeSeconds = null, int? EnrollmentMaxCount = null,
    int? RecoveryMaxLifetimeSeconds = null)
{
    public void Validate()
    {
        if (EnrollmentMaxLifetimeSeconds is null or <= 0 || EnrollmentMaxCount is null or <= 0 or > 100000 ||
            RecoveryMaxLifetimeSeconds is null or <= 0)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    public void CheckEnrollment(DateTimeOffset now, DateTimeOffset expires, int count)
    {
        Validate();
        if (expires <= now || expires > now.AddSeconds(EnrollmentMaxLifetimeSeconds!.Value) || count < 1 || count > EnrollmentMaxCount)
            throw new RequestRejectedException(RequestFailure.ValidationFailed);
    }
    public void CheckRecovery(DateTimeOffset now, DateTimeOffset expires)
    {
        Validate();
        if (expires <= now || expires > now.AddSeconds(RecoveryMaxLifetimeSeconds!.Value))
            throw new RequestRejectedException(RequestFailure.ValidationFailed);
    }
}

public sealed record AccessProof(ActorKind Kind, Guid Id, string Secret)
{ public override string ToString() => "AccessProof [redacted]"; }
public interface IAccessProofSource { AccessProof? Proof { get; } }
public sealed record AccessIdentity(ActorKind Kind, Guid SubjectId, Guid SoftwareId, Guid? InstanceId);
public sealed record InstanceIdentity(Guid Id, Guid SoftwareId, Guid DeviceId, string Lifecycle, long Revision);
public sealed record GrantView(Guid Id, Guid SoftwareId, IReadOnlyList<Guid>? DeviceIds, Guid? InstanceId, string State,
    DateTimeOffset ExpiresAt, int MaxInstances, int UsedCount, long Revision);
public sealed record CredentialView(Guid Id, Guid SubjectId, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, long Revision);
public sealed record RegistrationResult(Guid InstanceId, Guid CredentialId, Guid SoftwareId, long StreamEpoch);
public sealed record StreamResult(long StreamEpoch, int NextReportSeq = 1);
public sealed record DatabaseItem(string DatabaseKey, string SchemaId);
public sealed record DatabaseState(string Mode, IReadOnlyList<DatabaseItem> Items);
public sealed record StateReport(long StreamEpoch, long ReportSeq, DateTimeOffset ReportedAt, string InstallationState,
    Guid? InstalledReleaseId, string? InstalledVersion, DateTimeOffset? InstalledAt, string RunningState,
    IReadOnlyList<string> ReportedIps, DatabaseState DatabaseState);
public sealed record ReportResult(bool Applied, long CurrentEpoch, long CurrentReportSeq, DateTimeOffset? LastAcceptedAt, Guid? EvidenceId);
public sealed record ClientContext(Guid InstanceId, Guid SoftwareId, Guid SiteId, string SiteTimeZone, long CurrentEpoch,
    long LastReportSeq, DateTimeOffset? LastAcceptedAt, int PollRetrySeconds = 60, int MaxRequestBytes = 8192, int MaxPageSize = 200,
    Guid? LatestAvailableFormalReleaseId = null);
public sealed record InstanceLocation(Guid SiteId, string SiteName, Guid ProcessId, string ProcessCode, string ProcessName);
public sealed record InstanceView(Guid Id, Guid SoftwareId, Guid DeviceId, string DeviceNo, string DeviceName, InstanceLocation Location,
    string Lifecycle, StateReport? LastSnapshot, DateTimeOffset? LastAcceptedAt, long? UnreportedSeconds, string Freshness,
    Guid? LatestAvailableFormalReleaseId, Guid? LatestTaskId, string? LatestTaskResult, long Revision);
public sealed record InstallationHistoryView(Guid Id, Guid InstanceId, string InstallationState, Guid? InstalledReleaseId,
    string? InstalledVersion, DateTimeOffset? InstalledAt, DateTimeOffset ReceivedAt, string ReportedRunningState);
public sealed record InstanceFilter(Guid SoftwareId, Guid? ProcessId = null, Guid? DeviceId = null, string? DeviceNo = null,
    string? ReportedIp = null, Guid? InstalledReleaseId = null, string? Freshness = null, string? RunningState = null, string? Lifecycle = null);
public sealed record InstanceListInput(InstanceFilter Filter, int PageSize = 50, Guid? After = null);
public sealed record InstancePage<T>(IReadOnlyList<T> Items, Guid? Next);

/// <summary>IAM owns grants, machine identities and credentials; only hashes are persisted.</summary>
public interface IInstanceAccess
{
    Task<AccessIdentity?> AuthenticateAsync(AccessProof proof, bool protect, CancellationToken token);
    Task<Guid?> SoftwareForAsync(string resource, Guid id, bool protect, CancellationToken token);
    Task VerifyRegistrationAsync(RegisterInstanceCommand input, bool protect, CancellationToken token);
    Task VerifyRecoveryAsync(RecoverInstanceCommand input, bool protect, CancellationToken token);
    Task<GrantView> CreateEnrollmentAsync(CreateEnrollmentGrantCommand input, CancellationToken token);
    Task<GrantView> RevokeEnrollmentAsync(Guid id, long revision, CancellationToken token);
    Task<GrantView> CreateRecoveryAsync(CreateRecoveryGrantCommand input, CancellationToken token);
    Task<GrantView> RevokeRecoveryAsync(Guid id, long revision, CancellationToken token);
    Task<CredentialView> RevokeCredentialAsync(Guid id, long revision, CancellationToken token);
    Task<GrantView> GrantAsync(Guid id, bool recovery, CancellationToken token);
    Task<CredentialView> CredentialAsync(Guid id, CancellationToken token);
    Task<RegistrationResult?> FindRegistrationAsync(RegisterInstanceCommand input, CancellationToken token);
    Task<RegistrationResult?> FindRecoveryAsync(RecoverInstanceCommand input, CancellationToken token);
    Task<RegistrationResult> RegisterAsync(RegisterInstanceCommand input, Guid instanceId, CancellationToken token);
    Task<RegistrationResult> RecoverAsync(RecoverInstanceCommand input, long epoch, CancellationToken token);
}
public interface IManagedInstances
{
    Task VerifyInstallationEvidenceAsync(Guid evidenceId, Guid softwareId, Guid releaseId, string version, CancellationToken token);
    Task<InstanceIdentity?> GetIdentityAsync(Guid id, bool protect, CancellationToken token);
    Task CreateAsync(Guid id, Guid softwareId, Guid deviceId, Guid installationKey, CancellationToken token);
    Task<long> InvalidateStreamAsync(Guid instanceId, CancellationToken token);
    Task<InstanceIdentity> LifecycleAsync(Guid id, long revision, string lifecycle, CancellationToken token);
    Task<ClientContext> ContextAsync(Guid id, CancellationToken token);
    Task<StreamResult> OpenStreamAsync(Guid id, long expectedEpoch, CancellationToken token);
    Task<StreamResult> StreamResultAsync(Guid operationId, CancellationToken token);
    Task<ReportResult> ReportAsync(Guid id, StateReport report, CancellationToken token);
    Task<ReportResult?> FindReportAsync(Guid id, StateReport report, CancellationToken token);
}
public interface IInstanceQueries
{
    Task<InstanceView?> GetAsync(Guid id, CancellationToken token, string permission = "instance.read");
    Task<InstancePage<InstanceView>> ListAsync(InstanceListInput input, CancellationToken token);
    Task<InstancePage<InstallationHistoryView>> HistoryAsync(Guid id, int size, Guid? after, CancellationToken token);
    Task<InstancePage<GrantView>> GrantsAsync(Guid softwareId, int size, Guid? after, CancellationToken token);
    Task<InstancePage<CredentialView>> CredentialsAsync(Guid instanceId, int size, Guid? after, CancellationToken token);
}
public static class InstanceValidation
{
    public static bool Secret(string? value) => value is { Length: >= 43 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
        TryDecode(value);
    private static bool TryDecode(string value)
    {
        try { return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4-value.Length%4)%4)).Length >= 32; }
        catch (FormatException) { return false; }
    }
    public static bool Report(StateReport? x) => x is not null && x.StreamEpoch > 0 && x.ReportSeq > 0 && x.ReportedAt != default &&
        x.InstallationState is "Installed" or "NotInstalled" or "Unknown" && x.RunningState is "Running" or "Stopped" or "Unknown" &&
        (x.InstallationState != "Installed" || !string.IsNullOrWhiteSpace(x.InstalledVersion)) &&
        (x.InstallationState != "NotInstalled" || x.InstalledVersion is null && x.InstalledReleaseId is null && x.InstalledAt is null) &&
        (x.InstalledVersion is null || x.InstalledVersion.Length <= 128 && !string.IsNullOrWhiteSpace(x.InstalledVersion)) &&
        x.InstalledReleaseId != Guid.Empty && x.ReportedIps is { Count: <= 16 } && x.ReportedIps.All(ip => IPAddress.TryParse(ip, out _)) &&
        x.ReportedIps.Distinct(StringComparer.Ordinal).Count() == x.ReportedIps.Count && x.DatabaseState is { } db &&
        db.Mode is "Unknown" or "None" or "Present" && db.Items is { Count: <= 16 } &&
        (db.Mode == "Present" ? db.Items.Count > 0 : db.Items.Count == 0) &&
        db.Items.All(i => i is not null && i.DatabaseKey is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(i.DatabaseKey) &&
            i.SchemaId is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(i.SchemaId)) && db.Items.Select(i => i.DatabaseKey).Distinct(StringComparer.Ordinal).Count() == db.Items.Count;
    public static string Freshness(DateTimeOffset? accepted, DateTimeOffset now) => accepted is null ? "NeverReported" : now - accepted.Value > TimeSpan.FromMinutes(5) ? "Unknown" : "Fresh";
}
