using Svm.SharedKernel.Domain;
namespace Svm.Core.Identity;

public sealed class PlcUploadCredential(StrongId<PlcUploadCredential> id, Guid softwareId, string name,
    string secretHash, string scopeJson, DateTimeOffset expiresAt, Guid createdBy, DateTimeOffset createdAt)
    : AggregateRoot<StrongId<PlcUploadCredential>>(id)
{
    public Guid SoftwareId { get; private set; } = softwareId;
    public string Name { get; private set; } = name;
    public string SecretHash { get; private set; } = secretHash;
    public string ScopeJson { get; private set; } = scopeJson;
    public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;
    public Guid CreatedBy { get; private set; } = createdBy;
    public DateTimeOffset CreatedAt { get; private set; } = createdAt;
    public DateTimeOffset? RevokedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public bool Valid(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
    public void Revoke(DateTimeOffset now) { if (RevokedAt is not null) throw new InvalidOperationException("Credential revoked."); RevokedAt = now; Revision++; }
}
public interface IPlcCredentialRepository
{
    Task<PlcUploadCredential?> GetAsync(Guid id, bool protect, CancellationToken token);
    void Add(PlcUploadCredential value);
}
