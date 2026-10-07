using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.Core.Identity;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersonnelTests
{
    [Fact]
    public async Task ConcurrentAndRepeatedSeedNeverResetsExistingIdentity()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync(seed: false);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.SeedAsync()));
        Assert.Single(results, x => x);
        Assert.Equal(1, await fixture.CountAsync("iam.users"));
        Assert.Equal(PermissionCatalog.Entries.Count, await fixture.CountAsync("iam.permission_catalog"));
        Assert.Equal(2, await fixture.CountAsync("iam.permissions"));
        Assert.Equal(1, await fixture.CountAsync("aud.events"));
        var initial = Assert.IsType<SessionGrant>((await fixture.LoginAsync()).Grant);
        Assert.True(initial.Person.MustChangePassword);
        Assert.Equal(new[] { "identity.manage", "software.create" }, initial.Person.Permissions.Select(p => p.Operation));
        var password = Guid.NewGuid().ToString("N");
        var changed = await fixture.SendAsync(new ChangePasswordCommand(fixture.Password, password), initial.Proof);
        Assert.False(changed.Grant!.Person.MustChangePassword);
        await fixture.ExecuteAsync("UPDATE iam.users SET \"IsEnabled\"=false; DELETE FROM iam.permissions;");
        Assert.False(await fixture.SeedAsync());
        Assert.Equal(0, await fixture.CountAsync("iam.permissions"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(fixture.Database.ReaderConnection, "SELECT \"IsEnabled\" FROM iam.users"));
        await using var provider = fixture.Provider(false);
        var hash = await PersistenceDatabase.ScalarAsync<string>(fixture.Database.ReaderConnection, "SELECT \"PasswordHash\" FROM iam.users");
        Assert.True(provider.GetRequiredService<IPersonnelCrypto>().VerifyPassword(hash, password));
        Assert.False(provider.GetRequiredService<IPersonnelCrypto>().VerifyPassword(hash, fixture.Password));
        var bytes = Convert.FromBase64String(hash);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(600000, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(5, 4)));
    }

    [Fact]
    public async Task SeedAuditFailureRollsBackMarkerCatalogAndAccount()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync(seed: false);
        var catalog = await fixture.CountAsync("iam.permission_catalog");
        await RejectAudit(fixture);
        Assert.Equal(PersistenceFailure.DependencyUnavailable, (await Assert.ThrowsAsync<PersistenceException>(() => fixture.SeedAsync())).Failure);
        foreach (var table in new[] { "iam.users", "iam.permissions", "iam.seed_markers", "aud.events" })
            Assert.Equal(0, await fixture.CountAsync(table));
        Assert.Equal(catalog, await fixture.CountAsync("iam.permission_catalog"));
        await fixture.ExecuteAsync("DROP TRIGGER reject_audit ON aud.events");
        Assert.True(await fixture.SeedAsync());
    }

    [Fact]
    public async Task LoginLogoutPasswordAndExpiryUseCurrentDatabaseFacts()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        var first = (await fixture.LoginAsync()).Grant!;
        var second = (await fixture.LoginAsync()).Grant!;
        Assert.NotNull(await fixture.AuthenticateAsync(first.Proof));
        Assert.Null(await fixture.AuthenticateAsync(new(first.Proof.SessionId, Guid.NewGuid(), first.Proof.Secret)));
        Assert.Null(await fixture.AuthenticateAsync(new(first.Proof.SessionId, first.Proof.SubjectId, "forged")));
        var rejected = await fixture.SendAsync(new ChangePasswordCommand("incorrect password", Guid.NewGuid().ToString("N")), first.Proof);
        Assert.Equal(RequestFailure.CredentialInvalid, rejected.Failure);
        Assert.NotNull(await fixture.AuthenticateAsync(first.Proof));
        var password = Guid.NewGuid().ToString("N");
        var changed = (await fixture.SendAsync(new ChangePasswordCommand(fixture.Password, password), first.Proof)).Grant!;
        Assert.Null(await fixture.AuthenticateAsync(first.Proof));
        Assert.Null(await fixture.AuthenticateAsync(second.Proof));
        Assert.NotNull(await fixture.AuthenticateAsync(changed.Proof));
        await fixture.SendAsync(new LogoutCommand(), changed.Proof);
        Assert.Null(await fixture.AuthenticateAsync(changed.Proof));
        var again = (await fixture.LoginAsync(password)).Grant!;
        await fixture.ExecuteAsync("UPDATE iam.sessions SET \"ExpiresAt\"=clock_timestamp() - interval '1 second'");
        Assert.Null(await fixture.AuthenticateAsync(again.Proof));
    }

    [Fact]
    public async Task AuditFailureRollsBackPasswordSessionsAndRateCounters()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        var login = (await fixture.LoginAsync()).Grant!;
        var sessions = await fixture.CountAsync("iam.sessions");
        await RejectAudit(fixture);
        await Assert.ThrowsAsync<PersistenceException>(() => fixture.SendAsync(new ChangePasswordCommand(fixture.Password, Guid.NewGuid().ToString("N")), login.Proof));
        Assert.True((await fixture.AuthenticateAsync(login.Proof))!.MustChangePassword);
        Assert.Equal(sessions, await fixture.CountAsync("iam.sessions"));
        await Assert.ThrowsAsync<PersistenceException>(() => fixture.SendAsync(new LogoutCommand(), login.Proof));
        Assert.NotNull(await fixture.AuthenticateAsync(login.Proof));
        await Assert.ThrowsAsync<PersistenceException>(() => fixture.LoginAsync("incorrect password"));
        Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(fixture.Database.ReaderConnection, "SELECT COALESCE(sum(\"Failures\"),0)::bigint FROM iam.login_limits"));
    }

    [Fact]
    public async Task SharedAccountAndAddressLimitsPersistAndExpire()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        for (var i = 0; i < 5; i++) Assert.Equal(RequestFailure.CredentialInvalid, (await fixture.LoginAsync("incorrect password", address: "node-" + i)).Failure);
        var limited = await fixture.LoginAsync();
        Assert.Equal(RequestFailure.RateLimited, limited.Failure);
        Assert.InRange(limited.RetryAfterSeconds, 1, 900);
        await fixture.ExecuteAsync("UPDATE iam.login_limits SET \"WindowEnd\"=clock_timestamp() - interval '1 second'");
        Assert.NotNull((await fixture.LoginAsync()).Grant);
        for (var i = 0; i < 30; i++) Assert.Equal(RequestFailure.CredentialInvalid, (await fixture.LoginAsync("incorrect password", "UNKNOWN-" + i, "same-source")).Failure);
        var addressLimited = await fixture.LoginAsync(address: "same-source");
        Assert.Equal(RequestFailure.RateLimited, addressLimited.Failure);
        Assert.InRange(addressLimited.RetryAfterSeconds, 1, 300);
    }

    [Fact]
    public async Task FirstChangeRestrictionAndSoftwareScopeCannotUseGlobalPermission()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        var initial = (await fixture.LoginAsync()).Grant!;
        await using (var provider = fixture.Provider(false, initial.Proof))
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal(RequestFailure.PermissionDenied, (await Authorize(scope.ServiceProvider, initial.Proof, RequestScope.Global, "identity.manage")).Failure);
        }
        var grant = (await fixture.SendAsync(new ChangePasswordCommand(fixture.Password, Guid.NewGuid().ToString("N")), initial.Proof)).Grant!;
        await using (var provider = fixture.Provider(false, grant.Proof))
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Null((await Authorize(scope.ServiceProvider, grant.Proof, RequestScope.Global, "identity.manage")).Failure);
            Assert.Equal(RequestFailure.ResourceNotFound, (await Authorize(scope.ServiceProvider, grant.Proof, RequestScope.Software, "release.publish")).Failure);
        }
        await fixture.ExecuteAsync("DELETE FROM iam.permissions WHERE \"Operation\"='identity.manage'");
        await using (var provider = fixture.Provider(false, grant.Proof))
        await using (var scope = provider.CreateAsyncScope())
            Assert.Equal(RequestFailure.PermissionDenied, (await Authorize(scope.ServiceProvider, grant.Proof, RequestScope.Global, "identity.manage")).Failure);
    }

    [Fact]
    public async Task SubjectGuardBlocksRevocationUntilProtectedOperationFinishes()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        var login = (await fixture.LoginAsync()).Grant!;
        await using var provider = fixture.Provider(false, login.Proof);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async token =>
        {
            await scope.ServiceProvider.GetRequiredService<IPersonnelRepository>().LockSubjectAsync(login.Proof.SubjectId, false, token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(
                "BEGIN; SET LOCAL lock_timeout='200ms'; UPDATE iam.subject_guards SET \"Revision\"=\"Revision\"+1; COMMIT;"));
            Assert.Equal("55P03", error.SqlState);
            return true;
        }, default);
        await fixture.ExecuteAsync("UPDATE iam.subject_guards SET \"Revision\"=\"Revision\"+1");
    }

    [Fact]
    public async Task CommitAcknowledgementLossDoesNotReplaySeed()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync(seed: false);
        var interceptor = new LostCommit();
        var error = await Assert.ThrowsAsync<PersistenceException>(() => fixture.SeedAsync(interceptor: interceptor));
        Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, error.Failure);
        Assert.Equal(1, interceptor.Commits);
        Assert.Equal(1, await fixture.CountAsync("iam.users"));
        Assert.Equal(1, await fixture.CountAsync("aud.events"));
        Assert.False(await fixture.SeedAsync());
    }

    private static ValueTask<AuthorizationDecision> Authorize(IServiceProvider services, SessionProof proof, RequestScope scope, string permission) =>
        services.GetRequiredService<IRequestAuthorizer>().AuthorizeAsync(new AuthorizationRequest(new object(),
            new RequestPolicy(new RequestPolicyAttribute("test.authorization", ModuleOwner.Identity, RequestKind.Manage, scope,
                TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human) { Permission = permission }),
            new CallContextSnapshot(new CallActor(ActorKind.Human, proof.SubjectId), RequestKind.Manage, "test-authorization")), default);

    internal static Task RejectAudit(PersonnelDatabase fixture) => fixture.ExecuteAsync("""
        CREATE FUNCTION aud.reject_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test audit unavailable'; END $$;
        CREATE TRIGGER reject_audit BEFORE INSERT ON aud.events FOR EACH ROW EXECUTE FUNCTION aud.reject_insert();
        """);
    private sealed class LostCommit : DbTransactionInterceptor
    {
        public int Commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { Commits++; throw new IOException("Simulated lost commit acknowledgement"); }
    }
}
