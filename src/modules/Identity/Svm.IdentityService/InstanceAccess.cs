using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Core.Identity;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;

namespace Svm.IdentityService;

public static class InstanceAccessRegistration
{
    public static IServiceCollection AddSvmInstanceAccess(this IServiceCollection services)
    {
        services.TryAddSingleton(new InstanceAccessOptions());
        return services.AddScoped<IInstanceAccess, InstanceAccess>();
    }
}
internal sealed class InstanceAccess(IInstanceAccessRepository repository, IPersonnelCrypto crypto, IAccessProofSource proofs,
    IUnitOfWork unitOfWork, TimeProvider clock, InstanceAccessOptions options) : IInstanceAccess
{
    private DateTimeOffset Now => clock.GetUtcNow();
    public async Task<AccessIdentity?> AuthenticateAsync(AccessProof proof, bool protect, CancellationToken token)
    {
        if (proof.Kind == ActorKind.Instance)
        {
            var credential = await repository.CredentialAsync(proof.Id, false, token);
            if (credential is null) return null;
            var subject = await repository.SubjectAsync(credential.SubjectId.Value, protect, token);
            if (subject is null) return null;
            credential = await repository.CredentialAsync(proof.Id, false, token);
            return credential is not null && credential.Valid(Now) && crypto.VerifySecret(credential.SecretHash, proof.Secret)
                ? new(ActorKind.Instance, subject.Id.Value, subject.SoftwareId, subject.Id.Value) : null;
        }
        if (proof.Kind == ActorKind.EnrollmentGrant)
        {
            var grant = await repository.EnrollmentAsync(proof.Id, protect, token);
            return grant is not null && crypto.VerifySecret(grant.SecretHash, proof.Secret) ? new(proof.Kind, grant.Id.Value, grant.SoftwareId, null) : null;
        }
        if (proof.Kind == ActorKind.RecoveryGrant)
        {
            var grant = await repository.RecoveryAsync(proof.Id, false, token);
            if (grant is null) return null;
            if (protect) await repository.SubjectAsync(grant.InstanceId.Value, true, token);
            grant = await repository.RecoveryAsync(proof.Id, protect, token);
            return grant is not null && crypto.VerifySecret(grant.SecretHash, proof.Secret) ? new(proof.Kind, grant.Id.Value, grant.SoftwareId, grant.InstanceId.Value) : null;
        }
        return null;
    }
    public async Task<Guid?> SoftwareForAsync(string resource, Guid id, bool protect, CancellationToken token)
    {
        return resource switch
        {
            "enrollment" => (await repository.EnrollmentAsync(id, protect, token))?.SoftwareId,
            "recovery" => (await repository.RecoveryAsync(id, false, token))?.SoftwareId,
            "credential" => await CredentialSoftwareAsync(id, protect, token),
            "instance" => (await repository.SubjectAsync(id, protect, token))?.SoftwareId,
            _ => throw new RequestRejectedException(RequestFailure.ConfigurationInvalid)
        };
    }
    private async Task<Guid?> CredentialSoftwareAsync(Guid id, bool protect, CancellationToken token)
    {
        var c = await repository.CredentialAsync(id, false, token);
        return c is null ? null : (await repository.SubjectAsync(c.SubjectId.Value, protect, token))?.SoftwareId;
    }
    private async Task<EnrollmentPermission> EnrollmentProofAsync(bool protect, CancellationToken token)
    {
        var proof = proofs.Proof;
        if (proof is null || proof.Kind != ActorKind.EnrollmentGrant || await AuthenticateAsync(proof, protect, token) is null)
            throw new RequestRejectedException(RequestFailure.CredentialInvalid);
        return await repository.EnrollmentAsync(proof.Id, protect, token) ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid);
    }
    public async Task VerifyRegistrationAsync(RegisterInstanceCommand input, bool protect, CancellationToken token)
    {
        var grant = await EnrollmentProofAsync(protect, token);
        if (grant.SoftwareId != input.SoftwareId) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var row = await repository.RegistrationAsync(input.SoftwareId, input.InstallationKey, grant.Id.Value, input.Key, protect, token);
        if (row is not null) { await VerifyOriginalAsync(row, grant.Id.Value, input, protect, token); return; }
        RequireGrant(grant.State(Now));
        if (!grant.DeviceIds.Contains(input.DeviceId)) throw new RequestRejectedException(RequestFailure.PermissionDenied);
    }
    private async Task VerifyOriginalAsync(InstanceRegistration row, Guid grantId, RegisterInstanceCommand input, bool protect, CancellationToken token)
    {
        if (row.GrantId.Value != grantId || row.Key != input.Key || row.RequestDigest != RegistrationDigest(input))
            throw new RequestRejectedException(RequestFailure.RegistrationConflict);
        await repository.SubjectAsync(row.InstanceId, protect, token);
        var credential = await repository.CredentialAsync(row.CredentialId, false, token);
        if (credential is null || !credential.Valid(Now) || !crypto.VerifySecret(credential.SecretHash, input.SecretMaterial))
            throw new RequestRejectedException(RequestFailure.CredentialInvalid);
    }
    public async Task<RegistrationResult?> FindRegistrationAsync(RegisterInstanceCommand input, CancellationToken token)
    {
        var grant = await EnrollmentProofAsync(unitOfWork.CurrentOperationId is not null, token);
        var row = await repository.RegistrationAsync(input.SoftwareId, input.InstallationKey, grant.Id.Value, input.Key, unitOfWork.CurrentOperationId is not null, token);
        if (row is null) return null;
        await VerifyOriginalAsync(row, grant.Id.Value, input, unitOfWork.CurrentOperationId is not null, token);
        return new(row.InstanceId, row.CredentialId, row.SoftwareId, 0);
    }
    public async Task<RegistrationResult> RegisterAsync(RegisterInstanceCommand input, Guid instanceId, CancellationToken token)
    {
        RequireWrite(); await VerifyRegistrationAsync(input, true, token);
        var grant = await EnrollmentProofAsync(true, token); RequireGrant(grant.State(Now)); grant.Consume(Now);
        var credential = new InstanceCredential(Guid.NewGuid(), new(instanceId), crypto.HashSecret(input.SecretMaterial));
        repository.AddSubject(new(new(instanceId), input.SoftwareId)); repository.AddCredential(credential);
        repository.AddRegistration(new(Guid.NewGuid(), grant.Id, input.SoftwareId, input.InstallationKey, input.DeviceId, input.Key, RegistrationDigest(input), instanceId, credential.Id));
        return new(instanceId, credential.Id, input.SoftwareId, 0);
    }
    private async Task<RecoveryPermission> RecoveryProofAsync(bool protect, CancellationToken token)
    {
        var proof = proofs.Proof;
        if (proof is null || proof.Kind != ActorKind.RecoveryGrant || await AuthenticateAsync(proof, protect, token) is null)
            throw new RequestRejectedException(RequestFailure.CredentialInvalid);
        return await repository.RecoveryAsync(proof.Id, protect, token) ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid);
    }
    public async Task VerifyRecoveryAsync(RecoverInstanceCommand input, bool protect, CancellationToken token)
    {
        var grant = await RecoveryProofAsync(protect, token);
        if (grant.UsedKey is not null)
        {
            if (grant.UsedKey != input.Key || grant.RequestDigest != RecoveryDigest(input)) throw new RequestRejectedException(RequestFailure.RegistrationConflict);
            var c = await repository.CredentialAsync(grant.CredentialId!.Value, false, token);
            if (c is null || !c.Valid(Now) || !crypto.VerifySecret(c.SecretHash, input.SecretMaterial)) throw new RequestRejectedException(RequestFailure.CredentialInvalid);
            return;
        }
        RequireGrant(grant.State(Now));
    }
    public async Task<RegistrationResult?> FindRecoveryAsync(RecoverInstanceCommand input, CancellationToken token)
    {
        var grant = await RecoveryProofAsync(unitOfWork.CurrentOperationId is not null, token);
        if (grant.UsedKey is null) return null;
        await VerifyRecoveryAsync(input, unitOfWork.CurrentOperationId is not null, token);
        return new(grant.InstanceId.Value, grant.CredentialId!.Value, grant.SoftwareId, grant.ResultEpoch!.Value);
    }
    public async Task<RegistrationResult> RecoverAsync(RecoverInstanceCommand input, long epoch, CancellationToken token)
    {
        RequireWrite(); await VerifyRecoveryAsync(input, true, token);
        var grant = await RecoveryProofAsync(true, token); RequireGrant(grant.State(Now));
        await repository.RevokeCredentialsAsync(grant.InstanceId.Value, Now, token);
        var c = new InstanceCredential(Guid.NewGuid(), grant.InstanceId, crypto.HashSecret(input.SecretMaterial));
        repository.AddCredential(c); grant.Consume(input.Key, RecoveryDigest(input), c.Id, epoch, Now);
        return new(grant.InstanceId.Value, c.Id, grant.SoftwareId, epoch);
    }
    public Task<GrantView> CreateEnrollmentAsync(CreateEnrollmentGrantCommand x, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RequireWrite(); options.CheckEnrollment(Now, x.ExpiresAt, x.MaxInstances);
        var grant = new EnrollmentPermission(new(Guid.NewGuid()), x.SoftwareId, x.DeviceIds.ToArray(), crypto.HashSecret(x.SecretMaterial), x.ExpiresAt, x.MaxInstances);
        repository.AddEnrollment(grant); return Task.FromResult(View(grant));
    }
    public async Task<GrantView> RevokeEnrollmentAsync(Guid id, long revision, CancellationToken token)
    { RequireWrite(); var g=await repository.EnrollmentAsync(id,true,token) ?? throw Missing(); Revision(g.Revision,revision); g.Revoke(Now); return View(g); }
    public async Task<GrantView> CreateRecoveryAsync(CreateRecoveryGrantCommand x, CancellationToken token)
    {
        RequireWrite(); var subject=await repository.SubjectAsync(x.InstanceId,true,token) ?? throw Missing();
        options.CheckRecovery(Now, x.ExpiresAt);
        var g=new RecoveryPermission(new(Guid.NewGuid()),subject.SoftwareId,subject.Id,crypto.HashSecret(x.SecretMaterial),x.ExpiresAt); repository.AddRecovery(g); return View(g);
    }
    public async Task<GrantView> RevokeRecoveryAsync(Guid id, long revision, CancellationToken token)
    {
        RequireWrite(); var row=await repository.RecoveryAsync(id,false,token) ?? throw Missing(); await repository.SubjectAsync(row.InstanceId.Value,true,token);
        var g=await repository.RecoveryAsync(id,true,token) ?? throw Missing(); Revision(g.Revision,revision); g.Revoke(Now); return View(g);
    }
    public async Task<CredentialView> RevokeCredentialAsync(Guid id, long revision, CancellationToken token)
    {
        RequireWrite(); var row=await repository.CredentialAsync(id,false,token) ?? throw Missing(); await repository.SubjectAsync(row.SubjectId.Value,true,token);
        var c=await repository.CredentialAsync(id,true,token) ?? throw Missing(); Revision(c.Revision,revision); c.Revoke(Now); return View(c);
    }
    public async Task<GrantView> GrantAsync(Guid id, bool recovery, CancellationToken token) => recovery ? View(await repository.RecoveryAsync(id,false,token) ?? throw Missing()) : View(await repository.EnrollmentAsync(id,false,token) ?? throw Missing());
    public async Task<CredentialView> CredentialAsync(Guid id, CancellationToken token) => View(await repository.CredentialAsync(id,false,token) ?? throw Missing());
    private GrantView View(EnrollmentPermission g) => new(g.Id.Value,g.SoftwareId,g.DeviceIds,null,g.State(Now),g.ExpiresAt,g.MaxInstances,g.UsedCount,g.Revision);
    private GrantView View(RecoveryPermission g) => new(g.Id.Value,g.SoftwareId,null,g.InstanceId.Value,g.State(Now),g.ExpiresAt,1,g.UsedKey is null ? 0 : 1,g.Revision);
    private static CredentialView View(InstanceCredential c) => new(c.Id,c.SubjectId.Value,c.ExpiresAt,c.RevokedAt,c.Revision);
    private string RegistrationDigest(RegisterInstanceCommand x) => Hash(JsonSerializer.Serialize(new { x.SoftwareId,x.InstallationKey,x.DeviceId,secretHash=crypto.HashSecret(x.SecretMaterial) }));
    private string RecoveryDigest(RecoverInstanceCommand x) => Hash(crypto.HashSecret(x.SecretMaterial));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Revision(long actual,long expected) { if(actual!=expected) throw new RequestRejectedException(RequestFailure.RevisionConflict); }
    private static void RequireGrant(string state)
    { if(state!="Active") throw new RequestRejectedException(state switch { "Expired"=>RequestFailure.GrantExpired,"Exhausted"=>RequestFailure.GrantExhausted,_=>RequestFailure.GrantRevoked }); }
    private static RequestRejectedException Missing() => new(RequestFailure.ResourceNotFound);
    private void RequireWrite() { if(unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
