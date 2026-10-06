using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Identity;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.SharedKernel.Domain;

namespace Svm.IdentityService;

public static class IdentityRegistration
{
    public static IServiceCollection AddSvmPersonnel(this IServiceCollection services) => services.AddScoped<IPersonnelService, PersonnelService>();
}

internal sealed class PersonnelService(IPersonnelRepository repository, IPersonnelCrypto crypto, PersonnelPolicy policy) : IPersonnelService
{
    public async Task<PersonnelView?> AuthenticateAsync(SessionProof proof, bool protect, CancellationToken cancellationToken)
    {
        if (protect) await repository.LockSubjectAsync(proof.SubjectId, true, cancellationToken);
        var person = await repository.GetAsync(proof.SubjectId, cancellationToken);
        var session = await repository.SessionAsync(proof.SessionId, cancellationToken);
        var now = await repository.NowAsync(cancellationToken);
        if (person is not { IsEnabled: true } || session is null || session.SubjectId != proof.SubjectId ||
            session.RevokedAt is not null || session.ExpiresAt <= now || !crypto.VerifySecret(session.SecretHash, proof.Secret)) return null;
        return await ViewAsync(person, cancellationToken);
    }

    public async Task<PersonnelMutation> LoginAsync(string employeeNo, string password, string address, CancellationToken cancellationToken)
    {
        var person = await repository.FindAsync(employeeNo, cancellationToken);
        if (person is not null)
        {
            await repository.LockSubjectAsync(person.Id.Value, true, cancellationToken);
            person = await repository.GetAsync(person.Id.Value, cancellationToken);
        }
        var limit = await LimitAsync(employeeNo, address, cancellationToken);
        if (limit > 0) return new() { Failure = RequestFailure.RateLimited, RetryAfterSeconds = limit };
        // A fixed-cost dummy hash prevents the unknown-account path from skipping password work.
        var valid = crypto.VerifyPassword(person?.PasswordHash ?? crypto.DummyPasswordHash, password);
        if (!valid || person is not { IsEnabled: true })
        {
            await FailAsync(employeeNo, address, cancellationToken);
            return new() { Failure = RequestFailure.CredentialInvalid, Changed = true };
        }
        return new() { Grant = await IssueAsync(person, cancellationToken), Person = await ViewAsync(person, cancellationToken), Changed = true };
    }

    public async Task<PersonnelMutation> LogoutAsync(SessionProof proof, CancellationToken cancellationToken)
    {
        var person = await AuthenticateAsync(proof, true, cancellationToken) ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var session = await repository.SessionAsync(proof.SessionId, cancellationToken) ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        session.Revoke(await repository.NowAsync(cancellationToken));
        await repository.AdvanceRevisionAsync(proof.SubjectId, cancellationToken);
        return new() { Person = person, Changed = true };
    }

    public async Task<PersonnelMutation> ChangePasswordAsync(SessionProof proof, string currentPassword, string newPassword, string address, CancellationToken cancellationToken)
    {
        var view = await AuthenticateAsync(proof, true, cancellationToken) ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var person = await repository.GetAsync(proof.SubjectId, cancellationToken) ?? throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
        var limit = await LimitAsync(view.EmployeeNo, address, cancellationToken);
        if (limit > 0) return new() { Failure = RequestFailure.RateLimited, RetryAfterSeconds = limit };
        if (!crypto.VerifyPassword(person.PasswordHash, currentPassword))
        {
            await FailAsync(view.EmployeeNo, address, cancellationToken);
            return new() { Person = view, Failure = RequestFailure.CredentialInvalid, Changed = true };
        }
        if (currentPassword == newPassword) throw new RequestRejectedException(RequestFailure.ValidationFailed);
        person.ChangePassword(crypto.HashPassword(newPassword));
        await repository.RevokeSessionsAsync(proof.SubjectId, await repository.NowAsync(cancellationToken), cancellationToken);
        await repository.AdvanceRevisionAsync(proof.SubjectId, cancellationToken);
        return new() { Grant = await IssueAsync(person, cancellationToken), Person = await ViewAsync(person, cancellationToken), Changed = true };
    }

    public async Task<PersonnelMutation> SeedAsync(string employeeNo, string displayName, string password, CancellationToken cancellationToken)
    {
        if (!await repository.BeginSeedAsync(cancellationToken)) return new();
        if (await repository.FindAsync(employeeNo, cancellationToken) is not null)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        await repository.EnsurePermissionCatalogAsync(PermissionCatalog.Entries, cancellationToken);
        var account = new UserAccount(new StrongId<UserAccount>(Guid.NewGuid()), employeeNo, displayName, crypto.HashPassword(password));
        repository.AddInitialAccount(account, ["identity.manage", "software.create"]);
        return new() { Person = new(account.Id.Value, employeeNo, displayName, true,
            [new(null, "identity.manage"), new(null, "software.create")]), Changed = true };
    }

    private async Task<SessionGrant> IssueAsync(UserAccount person, CancellationToken cancellationToken)
    {
        var secret = crypto.CreateSecret();
        var session = new WebSession { Id = Guid.NewGuid(), SubjectId = person.Id.Value, SecretHash = crypto.HashSecret(secret),
            ExpiresAt = (await repository.NowAsync(cancellationToken)).AddHours(policy.SessionHours) };
        repository.AddSession(session);
        return new(new SessionProof(session.Id, session.SubjectId, secret), await ViewAsync(person, cancellationToken), session.ExpiresAt);
    }

    private async Task<PersonnelView> ViewAsync(UserAccount user, CancellationToken cancellationToken) =>
        new(user.Id.Value, user.EmployeeNo, user.DisplayName, user.MustChangePassword,
            (await repository.PermissionsAsync(user.Id.Value, cancellationToken)).Select(p => new PermissionView(p.SoftwareId, p.Operation)).ToArray());
    private Task<int> LimitAsync(string account, string address, CancellationToken cancellationToken) =>
        repository.LockRateLimitAsync("a:" + crypto.HashSecret(account), "i:" + crypto.HashSecret(address),
            policy.AccountFailures, policy.AccountWindowSeconds, policy.AddressFailures, policy.AddressWindowSeconds, cancellationToken);
    private Task FailAsync(string account, string address, CancellationToken cancellationToken) =>
        repository.RecordFailureAsync("a:" + crypto.HashSecret(account), "i:" + crypto.HashSecret(address), cancellationToken);
}
