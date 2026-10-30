using System.Text.Json;
using Svm.Core.Identity;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Plc;
namespace Svm.IdentityService;

internal sealed class PlcUploadAccess(IPlcCredentialRepository repository, IPersonnelCrypto crypto, IUnitOfWork unit,
    TimeProvider clock, PlcOptions options) : IPlcUploadAccess
{
    public async Task<PlcCredentialView?> AuthenticateAsync(AccessProof proof, bool protect, CancellationToken token)
    {
        if (proof.Kind != ActorKind.PlcUpload) return null;
        var c = await repository.GetAsync(proof.Id, protect, token);
        return c is not null && c.Valid(clock.GetUtcNow()) && crypto.VerifySecret(c.SecretHash, proof.Secret) ? View(c) : null;
    }
    public async Task<PlcCredentialView> GetAsync(Guid id, bool protect, CancellationToken token) => View(await repository.GetAsync(id, protect, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound));
    public Task<PlcCredentialView> CreateAsync(CreatePlcCredentialCommand x, Guid actor, CancellationToken token)
    {
        Write(); token.ThrowIfCancellationRequested(); options.Check(clock.GetUtcNow(), x.ExpiresAt);
        var c = new PlcUploadCredential(new(Guid.NewGuid()), x.SoftwareId, x.Name, crypto.HashSecret(x.SecretMaterial),
            JsonSerializer.Serialize(x.Scope.OrderBy(s => s.BindingId)), x.ExpiresAt, actor, clock.GetUtcNow());
        repository.Add(c); return Task.FromResult(View(c));
    }
    public async Task<PlcCredentialView> RevokeAsync(Guid id, long revision, CancellationToken token)
    {
        Write(); var c = await repository.GetAsync(id, true, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (c.Revision != revision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (c.RevokedAt is not null) throw new RequestRejectedException(RequestFailure.InvalidState);
        c.Revoke(clock.GetUtcNow()); return View(c);
    }
    // This verifies durable provenance only. The worker can finish an already accepted archive after expiry/revocation.
    public async Task<bool> IsAcceptedUploaderAsync(Guid id, Guid softwareId, CancellationToken token) =>
        (await repository.GetAsync(id, false, token))?.SoftwareId == softwareId;
    private static PlcCredentialView View(PlcUploadCredential c) => new(c.Id.Value,c.SoftwareId,c.Name,
        JsonSerializer.Deserialize<PlcScope[]>(c.ScopeJson)!,c.ExpiresAt,c.RevokedAt,c.CreatedBy,c.CreatedAt,c.Revision);
    private void Write() { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}

internal sealed class PlcUploadProvenance(IPlcCredentialRepository repository) : IPlcUploadProvenance
{ public async Task<bool> IsAcceptedUploaderAsync(Guid subject, Guid softwareId, CancellationToken token) => (await repository.GetAsync(subject,false,token))?.SoftwareId == softwareId; }
