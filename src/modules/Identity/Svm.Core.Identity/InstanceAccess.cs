using Svm.SharedKernel.Domain;

namespace Svm.Core.Identity;

public sealed class MachineSubject(StrongId<MachineSubject> id, Guid softwareId) : AggregateRoot<StrongId<MachineSubject>>(id)
{
    public Guid SoftwareId { get; private set; } = softwareId;
}
public sealed class InstanceCredential(Guid id, StrongId<MachineSubject> subjectId, string secretHash)
{
    public Guid Id { get; private set; } = id;
    public StrongId<MachineSubject> SubjectId { get; private set; } = subjectId;
    public string SecretHash { get; private set; } = secretHash;
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public bool Valid(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
    public void Revoke(DateTimeOffset now) { if (RevokedAt is null) { RevokedAt = now; Revision++; } }
    public override string ToString() => "InstanceCredential [redacted]";
}
public sealed class EnrollmentPermission(StrongId<EnrollmentPermission> id, Guid softwareId, Guid[] deviceIds, string secretHash, DateTimeOffset expiresAt, int maxInstances)
    : AggregateRoot<StrongId<EnrollmentPermission>>(id)
{
    public Guid SoftwareId { get; private set; } = softwareId;
    public Guid[] DeviceIds { get; private set; } = deviceIds.ToArray();
    public string SecretHash { get; private set; } = secretHash;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
    public int MaxInstances { get; private set; } = maxInstances;
    public int UsedCount { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public string State(DateTimeOffset now) => RevokedAt is not null ? "Revoked" : ExpiresAt <= now ? "Expired" : UsedCount >= MaxInstances ? "Exhausted" : "Active";
    public void Consume(DateTimeOffset now)
    { if (State(now) != "Active") throw new InvalidOperationException("No enrollment capacity."); UsedCount++; Revision++; }
    public void Revoke(DateTimeOffset now) { if (RevokedAt is null) { RevokedAt = now; Revision++; } }
    public override string ToString() => "EnrollmentPermission [redacted]";
}
public sealed class InstanceRegistration(Guid id, StrongId<EnrollmentPermission> grantId, Guid softwareId, Guid installationKey, Guid deviceId,
    Guid key, string requestDigest, Guid instanceId, Guid credentialId)
{
    public Guid Id { get; private set; } = id;
    public StrongId<EnrollmentPermission> GrantId { get; private set; } = grantId;
    public Guid SoftwareId { get; private set; } = softwareId;
    public Guid InstallationKey { get; private set; } = installationKey;
    public Guid DeviceId { get; private set; } = deviceId;
    public Guid Key { get; private set; } = key;
    public string RequestDigest { get; private set; } = requestDigest;
    public Guid InstanceId { get; private set; } = instanceId;
    public Guid CredentialId { get; private set; } = credentialId;
    public override string ToString() => "InstanceRegistration [redacted]";
}
public sealed class RecoveryPermission(StrongId<RecoveryPermission> id, Guid softwareId, StrongId<MachineSubject> instanceId, string secretHash, DateTimeOffset expiresAt)
    : AggregateRoot<StrongId<RecoveryPermission>>(id)
{
    public Guid SoftwareId { get; private set; } = softwareId;
    public StrongId<MachineSubject> InstanceId { get; private set; } = instanceId;
    public string SecretHash { get; private set; } = secretHash;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
    public DateTimeOffset? RevokedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public Guid? UsedKey { get; private set; }
    public string? RequestDigest { get; private set; }
    public Guid? CredentialId { get; private set; }
    public long? ResultEpoch { get; private set; }
    public string State(DateTimeOffset now) => UsedKey is not null ? "Consumed" : RevokedAt is not null ? "Revoked" : ExpiresAt <= now ? "Expired" : "Active";
    public void Consume(Guid key, string digest, Guid credentialId, long epoch, DateTimeOffset now)
    { if (State(now) != "Active") throw new InvalidOperationException("Recovery already consumed or unavailable."); UsedKey=key; RequestDigest=digest; CredentialId=credentialId; ResultEpoch=epoch; Revision++; }
    public void Revoke(DateTimeOffset now) { if (RevokedAt is null) { RevokedAt=now; Revision++; } }
    public override string ToString() => "RecoveryPermission [redacted]";
}
public interface IInstanceAccessRepository
{
    Task<MachineSubject?> SubjectAsync(Guid id, bool protect, CancellationToken token);
    Task<InstanceCredential?> CredentialAsync(Guid id, bool protect, CancellationToken token);
    Task<EnrollmentPermission?> EnrollmentAsync(Guid id, bool protect, CancellationToken token);
    Task<RecoveryPermission?> RecoveryAsync(Guid id, bool protect, CancellationToken token);
    Task<InstanceRegistration?> RegistrationAsync(Guid softwareId, Guid installationKey, Guid grantId, Guid key, bool protect, CancellationToken token);
    Task RevokeCredentialsAsync(Guid subjectId, DateTimeOffset now, CancellationToken token);
    void AddSubject(MachineSubject value);
    void AddCredential(InstanceCredential value);
    void AddEnrollment(EnrollmentPermission value);
    void AddRecovery(RecoveryPermission value);
    void AddRegistration(InstanceRegistration value);
}
