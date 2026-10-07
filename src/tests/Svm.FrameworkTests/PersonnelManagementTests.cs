using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersonnelManagementTests
{
    [Fact]
    public async Task CreationHasNoImplicitGrantsAndStoresOnlySafeResultsAndOperatorAudit()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var secret = Guid.NewGuid().ToString("N");
        var request = new CreateUserCommand(Guid.NewGuid(), "NEW-USER", "新人员", secret);
        var result = await fixture.SendAsync(request);
        Assert.True(result.Value.IsEnabled); Assert.True(result.Value.MustChangePassword); Assert.Empty(result.Value.Permissions);
        Assert.Equal(1, result.Value.Revision);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result)); Assert.DoesNotContain(secret, request.ToString());
        var rows = await PersistenceDatabase.ScalarAsync<string>(fixture.Personnel.Database.ReaderConnection,
            "SELECT json_agg(r)::text FROM iam.operation_results r");
        PersistenceDatabase.AssertRedacted(rows, secret); Assert.DoesNotContain("temporaryPassword", rows);
        Assert.Equal(fixture.Proof.SubjectId.ToString(), await PersistenceDatabase.ScalarAsync<string>(fixture.Personnel.Database.ReaderConnection,
            "SELECT \"SubjectId\"::text FROM aud.events WHERE \"Operation\"='identity.users.create'"));
        var hash = await PersistenceDatabase.ScalarAsync<string>(fixture.Personnel.Database.ReaderConnection,
            "SELECT \"PasswordHash\" FROM iam.users WHERE \"EmployeeNo\"='NEW-USER'");
        Assert.NotEqual(secret, hash); Assert.DoesNotContain(hash, JsonSerializer.Serialize(await fixture.UserAsync(result.Value.Id)));
    }
    [Fact]
    public async Task ConcurrentSameKeyCreatesOneUserAndOneAudit()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var command = new CreateUserCommand(Guid.NewGuid(), "CONCURRENT", "并发人员", Guid.NewGuid().ToString("N"));
        var results = await Task.WhenAll(fixture.SendAsync(command), fixture.SendAsync(command));
        Assert.Equal(results[0].OperationId, results[1].OperationId); Assert.Equal(results[0].Value.Id, results[1].Value.Id);
        Assert.Equal(2, await fixture.Personnel.CountAsync("iam.users")); Assert.Equal(1, await fixture.Personnel.CountAsync("iam.operation_results"));
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(fixture.Personnel.Database.ReaderConnection,
            "SELECT count(*) FROM aud.events WHERE \"Operation\"='identity.users.create'"));
    }
    [Fact]
    public async Task ConcurrentDuplicateEmployeeNumbersHaveOneSuccessfulCreation()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        async Task<string> Create()
        {
            try { await fixture.CreateUserAsync("DUPLICATE"); return "created"; }
            catch (RequestRejectedException error) { return error.Code; }
        }
        Assert.Equal(new[] { "INVALID_STATE", "created" }, (await Task.WhenAll(Create(), Create())).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, await fixture.Personnel.CountAsync("iam.users")); Assert.Equal(1, await fixture.Personnel.CountAsync("iam.operation_results"));
    }
    [Fact]
    public async Task ReplayPrecedesOldRevisionAndChangedBodyOrStaleNewOperationIsRejected()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var user = (await fixture.CreateUserAsync()).Value;
        var change = new UpdateUserCommand(Guid.NewGuid(), user.Id, user.Revision, "改名", null, "人员资料更新");
        var updated = await fixture.SendAsync(change); var replay = await fixture.SendAsync(change);
        Assert.Equal(updated.OperationId, replay.OperationId); Assert.Equal(user.Revision + 1, replay.Value.Revision);
        Assert.Equal(RequestFailure.IdempotencyConflict, (await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.SendAsync(change with { DisplayName = "其他名称" }))).Failure);
        Assert.Equal(RequestFailure.RevisionConflict, (await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.SendAsync(change with { Key = Guid.NewGuid() }))).Failure);
        Assert.Equal(2, await fixture.Personnel.CountAsync("iam.operation_results"));
    }
    [Fact]
    public async Task SoftwarePermissionsMustBePreservedExactlyAndFactoryPermissionsCanChange()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var user = (await fixture.CreateUserAsync()).Value; var software = Guid.NewGuid();
        await fixture.Personnel.ExecuteAsync($"INSERT INTO iam.permissions VALUES ('{Guid.NewGuid()}','{user.Id}','{software}','software.read')");
        var retained = new PermissionView(software, "software.read");
        var updated = await fixture.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision,
            [new(null, "asset.read"), new(null, "asset.manage"), retained], "台账职责分配"));
        Assert.Equal(3, updated.Value.Permissions.Count);
        foreach (var bad in new IReadOnlyList<PermissionView>[] { [], [new(null, "asset.read")], [retained, new(software, "release.upload")], [retained, new(null, "software.read")] })
            Assert.Equal(RequestFailure.ValidationFailed, (await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.SendAsync(
                new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, updated.Value.Revision, bad, "非法授权变更")))).Failure);
        Assert.Equal(3, (await fixture.UserAsync(user.Id)).Permissions.Count);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LastAdministratorCannotBeDisabledOrLoseManagementPermission(bool permissions)
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var admin = await fixture.UserAsync(fixture.Proof.SubjectId);
        ICommand<UserView> command = permissions ? new ReplaceUserPermissionsCommand(Guid.NewGuid(), admin.Id, admin.Revision, [], "撤权") :
            new UpdateUserCommand(Guid.NewGuid(), admin.Id, admin.Revision, null, false, "停用");
        Assert.Equal(RequestFailure.InvalidState, (await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.SendAsync(command))).Failure);
        Assert.Empty((await fixture.PageAsync(new(null, false, 50, null))).Items);
        Assert.Equal(0, await fixture.Personnel.CountAsync("iam.operation_results"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentAdministratorChangesRetainOneEffectiveAdministrator(bool permissions)
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var temporary = Guid.NewGuid().ToString("N");
        var user = (await fixture.SendAsync(new CreateUserCommand(Guid.NewGuid(), "SECOND-ADMIN", "第二管理员", temporary))).Value;
        await fixture.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision, [new(null, "identity.manage")], "管理员分配"));
        var login = (await fixture.Personnel.LoginAsync(temporary, user.EmployeeNo)).Grant!;
        var changed = (await fixture.Personnel.SendAsync(new ChangePasswordCommand(temporary, Guid.NewGuid().ToString("N")), login.Proof)).Grant!;
        var a = await fixture.UserAsync(fixture.Proof.SubjectId); var b = await fixture.UserAsync(user.Id);
        async Task<string> Remove(UserView target, SessionProof proof)
        {
            ICommand<UserView> command = permissions ? new ReplaceUserPermissionsCommand(Guid.NewGuid(), target.Id, target.Revision, [], "并发撤权") :
                new UpdateUserCommand(Guid.NewGuid(), target.Id, target.Revision, null, false, "并发停用");
            try { await fixture.SendAsync(command, proof); return "changed"; }
            catch (RequestRejectedException error) { return error.Code; }
        }
        var result = await Task.WhenAll(Remove(a, fixture.Proof), Remove(b, changed.Proof)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { "INVALID_STATE", "changed" }, result.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(fixture.Personnel.Database.ReaderConnection,
            "SELECT count(*) FROM iam.users u WHERE u.\"IsEnabled\" AND EXISTS(SELECT 1 FROM iam.permissions p WHERE p.\"SubjectId\"=u.\"Id\" AND p.\"SoftwareId\" IS NULL AND p.\"Operation\"='identity.manage')"));
    }
    [Fact]
    public async Task AuthorizationRevocationRejectsCommittedReplayAndFirstChangeCannotManage()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var command = new CreateUserCommand(Guid.NewGuid(), "REPLAY", "人员", Guid.NewGuid().ToString("N"));
        await fixture.SendAsync(command);
        await fixture.Personnel.ExecuteAsync($"DELETE FROM iam.permissions WHERE \"SubjectId\"='{fixture.Proof.SubjectId}' AND \"Operation\"='identity.manage'");
        Assert.Equal(RequestFailure.PermissionDenied, (await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.SendAsync(command))).Failure);
        Assert.Equal(1, await fixture.Personnel.CountAsync("iam.operation_results"));
        var first = await PersonnelDatabase.CreateAsync(); var proof = (await first.LoginAsync()).Grant!.Proof;
        await using var context = new PersonnelManagementFixture(first, proof);
        Assert.Equal(RequestFailure.PermissionDenied, (await Assert.ThrowsAsync<RequestRejectedException>(() => context.CreateUserAsync())).Failure);
    }
    [Theory]
    [InlineData("audit")] [InlineData("save")] [InlineData("cancel")]
    public async Task BusinessAuditAndResultRollBackTogether(string stage)
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var before = await fixture.Personnel.CountAsync("aud.events");
        using var cancellation = new CancellationTokenSource();
        IInterceptor? interceptor = stage == "save" ? new SaveFailure() : stage == "cancel" ? new SaveCancellation(cancellation) : null;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.SendAsync(new CreateUserCommand(Guid.NewGuid(), "ROLLBACK", "回滚人员", Guid.NewGuid().ToString("N")),
            interceptor: interceptor, auditFailure: stage == "audit", token: cancellation.Token));
        Assert.Equal(1, await fixture.Personnel.CountAsync("iam.users")); Assert.Equal(before, await fixture.Personnel.CountAsync("aud.events"));
        Assert.Equal(0, await fixture.Personnel.CountAsync("iam.operation_results")); Assert.Equal(0, await fixture.Personnel.CountAsync("framework.\"OutboxMessage\""));
    }
    [Fact]
    public async Task LostCommitConfirmationRecoversSafeReferenceWithoutAnotherCreation()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var fault = new LostConfirmation();
        var command = new CreateUserCommand(Guid.NewGuid(), "COMMIT-UNKNOWN", "核实人员", Guid.NewGuid().ToString("N"));
        var result = await fixture.SendAsync(command, interceptor: fault);
        Assert.Equal(1, fault.Commits); Assert.Equal(result.Value.Id, (await fixture.SendAsync(command)).Value.Id);
        Assert.Equal(2, await fixture.Personnel.CountAsync("iam.users")); Assert.Equal(1, await fixture.Personnel.CountAsync("iam.operation_results"));
    }
    [Fact]
    public async Task LostConfirmationRechecksRevokedAuthorizationInANewScopeWithoutRerunning()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var command = new CreateUserCommand(Guid.NewGuid(), "REVOKED-RECOVERY", "核实人员", Guid.NewGuid().ToString("N"));
        var fault = new RevokeAfterCommit(fixture);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.SendAsync(command, interceptor: fault));
        Assert.Equal(RequestFailure.PermissionDenied, error.Failure);
        Assert.Equal(1, fault.Commits); Assert.Equal(2, await fixture.Personnel.CountAsync("iam.users"));
        Assert.Equal(1, await fixture.Personnel.CountAsync("iam.operation_results"));
    }
    [Theory]
    [InlineData("disable")] [InlineData("reset")] [InlineData("permissions")]
    public async Task FailedAuditRollsBackAccountRevisionSessionsPermissionsAndResult(string operation)
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        var password = Guid.NewGuid().ToString("N");
        var user = (await fixture.SendAsync(new CreateUserCommand(Guid.NewGuid(), "ATOMIC-TARGET", "原资料", password))).Value;
        user = (await fixture.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision, [new(null, "asset.read")], "既有授权"))).Value;
        await fixture.Personnel.LoginAsync(password, user.EmployeeNo);
        const string snapshot = """
            SELECT json_build_object('users',(SELECT json_agg(u ORDER BY "Id") FROM iam.users u),
            'sessions',(SELECT json_agg(s ORDER BY "Id") FROM iam.sessions s),
            'guards',(SELECT json_agg(g ORDER BY "SubjectId") FROM iam.subject_guards g),
            'grants',(SELECT json_agg(p ORDER BY "Id") FROM iam.permissions p),
            'audit',(SELECT json_agg(a ORDER BY "Id") FROM aud.events a),
            'results',(SELECT json_agg(r ORDER BY "OperationId") FROM iam.operation_results r))::text
            """;
        var before = await PersistenceDatabase.ScalarAsync<string>(fixture.Personnel.Database.ReaderConnection, snapshot);
        ICommand<UserView> command = operation switch
        {
            "disable" => new UpdateUserCommand(Guid.NewGuid(), user.Id, user.Revision, "新资料", false, "停用核验"),
            "reset" => new ResetUserPasswordCommand(Guid.NewGuid(), user.Id, user.Revision, Guid.NewGuid().ToString("N"), "密码核验"),
            _ => new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision, [new(null, "asset.manage")], "授权核验")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SendAsync(command, auditFailure: true));
        Assert.Equal(before, await PersistenceDatabase.ScalarAsync<string>(fixture.Personnel.Database.ReaderConnection, snapshot));
    }
    [Fact]
    public async Task AdministrationFacadeCannotMutateOutsideAnOperation()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        await using var provider = fixture.Provider(); await using var scope = provider.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<IPersonnelAdministration>();
        var error = await Assert.ThrowsAsync<PersistenceException>(() =>
            administration.CreateAsync("NO-TRANSACTION", "事务外", Guid.NewGuid().ToString("N"), default));
        Assert.Equal(PersistenceFailure.InvalidTransactionNesting, error.Failure);
        Assert.Equal(1, await fixture.Personnel.CountAsync("iam.users"));
    }
    [Fact]
    public async Task ReadOnlyQueriesEscapePrefixWildcardsAndUseStablePagination()
    {
        await using var fixture = await PersonnelManagementFixture.CreateAsync();
        await fixture.CreateUserAsync("A_2"); await fixture.CreateUserAsync("A_1"); await fixture.CreateUserAsync("AB3");
        var first = await fixture.PageAsync(new("A_", null, 1, null)); Assert.Equal("A_1", Assert.Single(first.Items).EmployeeNo); Assert.NotNull(first.Next);
        var second = await fixture.PageAsync(new("A_", null, 1, first.Next)); Assert.Equal("A_2", Assert.Single(second.Items).EmployeeNo); Assert.Null(second.Next);
        Assert.Empty((await fixture.PageAsync(new(null, false, 50, null))).Items);
    }
    private sealed class SaveFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken token = default) =>
            throw new InvalidOperationException("fixture save failure");
    }
    private sealed class SaveCancellation(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken token = default)
        { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.FromResult(result); }
    }
    private sealed class LostConfirmation : DbTransactionInterceptor
    {
        internal int Commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken token = default)
        { Commits++; throw new IOException("fixture lost commit confirmation"); }
    }
    private sealed class RevokeAfterCommit(PersonnelManagementFixture fixture) : DbTransactionInterceptor
    {
        internal int Commits;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken token = default)
        {
            Commits++;
            await fixture.Personnel.ExecuteAsync($"DELETE FROM iam.permissions WHERE \"SubjectId\"='{fixture.Proof.SubjectId}' AND \"Operation\"='identity.manage'");
            throw new IOException("fixture lost confirmation after revocation");
        }
    }
}
