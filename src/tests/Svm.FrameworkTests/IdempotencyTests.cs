using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Idempotency;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class IdempotencyTests(PersistenceDatabase database) : IClassFixture<PersistenceDatabase>
{
    [Fact]
    public void ExplicitParsedFieldsNormalizeObjectsButPreserveTypesRouteAndArrayOrder()
    {
        var fixture = new IdempotencyFixture(database);
        var policy = fixture.Policy(); var target = AuthorizationTarget.Global(); var key = Guid.NewGuid();
        OperationRequestData Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new(key, OperationValue.Object(new OperationField("id", OperationValue.Identifier(key))), OperationValue.Object(
                root.EnumerateObject().Select(p => new OperationField(p.Name, p.Value.ValueKind switch
                {
                    JsonValueKind.String => OperationValue.Text(p.Value.GetString()!),
                    JsonValueKind.Number => OperationValue.Integer(p.Value.GetInt64()),
                    _ => throw new InvalidOperationException("Unsupported fixture field.")
                })).ToArray()));
        }
        var original = OperationDigest.Create(policy, target, Parse("{\"reason\":\"ok\",\"revision\":1}"));
        Assert.Equal(original, OperationDigest.Create(policy, target, Parse(" { \"revision\" : 1 , \"reason\" : \"ok\" } ")));
        Assert.NotEqual(original, OperationDigest.Create(policy, target, Parse("{\"reason\":\"ok\",\"revision\":\"1\"}")));
        var first = new OperationRequestData(key, OperationValue.Object(), OperationValue.Object(
            new OperationField("items", OperationValue.Array(OperationValue.Integer(1), OperationValue.Integer(2)))));
        var second = new OperationRequestData(key, OperationValue.Object(), OperationValue.Object(
            new OperationField("items", OperationValue.Array(OperationValue.Integer(2), OperationValue.Integer(1)))));
        Assert.NotEqual(OperationDigest.Create(policy, target, first), OperationDigest.Create(policy, target, second));
        var changedRoute = new OperationRequestData(key, OperationValue.Object(new OperationField("id", OperationValue.Identifier(Guid.NewGuid()))), OperationValue.Object(
            new OperationField("items", OperationValue.Array(OperationValue.Integer(1), OperationValue.Integer(2)))));
        Assert.NotEqual(OperationDigest.Create(policy, target, first), OperationDigest.Create(policy, target, changedRoute));
    }

    [Fact]
    public async Task ANewProviderReplaysCommittedReferencesAndConflictingContentNeverRunsHandler()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid();
        OperationResultReference first;
        await using (var provider = fixture.Provider())
            first = await fixture.ExecuteAsync(provider, key, action: (scope, token) => fixture.ApplyAsync(scope, token, accepted: true));
        await using (var provider = fixture.Provider())
        {
            var repeated = await fixture.ExecuteAsync(provider, key, OperationValue.Object(
                new("reason", OperationValue.Text("component test")), new("expectedRevision", OperationValue.Integer(0))),
                action: (_, _) => throw new InvalidOperationException("The revision has advanced; replay must not call the Handler."));
            Assert.Equal(first, repeated); Assert.Equal(OperationStatus.Accepted, repeated.Status); Assert.NotNull(repeated.WorkId);
            var conflict = await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.ExecuteAsync(provider, key,
                OperationValue.Object(new OperationField("expectedRevision", OperationValue.Integer(1)))));
            Assert.Equal(409, conflict.StatusCode); Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        }
        Assert.Equal(1, fixture.Calls); Assert.Equal(1, await fixture.RecordsAsync());
        Assert.Equal(1, await fixture.AuditsAsync()); Assert.Equal(1, await fixture.ProbeCountAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicateWaitsForTheOwningTransactionAndExecutesOnce()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid();
        var sql = new AcquisitionObserver();
        await using var provider = fixture.Provider(sql);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = fixture.ExecuteAsync(provider, key, action: async (scope, token) =>
        {
            started.SetResult(); await release.Task.WaitAsync(token);
            return await fixture.ApplyAsync(scope, token);
        }, cancellationToken: deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        var second = fixture.ExecuteAsync(provider, key, cancellationToken: deadline.Token);
        try { await sql.SecondAttempt.Task.WaitAsync(deadline.Token); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0], results[1]); Assert.Equal(1, fixture.Calls);
        Assert.Equal(1, await fixture.RecordsAsync()); Assert.Equal(1, await fixture.AuditsAsync()); Assert.Equal(1, await fixture.ProbeCountAsync());
    }

    [Fact]
    public async Task CancelingAWaitingDuplicateDoesNotCancelOrRepeatTheOwner()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid(); var observer = new AcquisitionObserver();
        await using var provider = fixture.Provider(observer);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ownerDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = fixture.ExecuteAsync(provider, key, action: async (scope, token) =>
        {
            started.SetResult(); await release.Task.WaitAsync(token); return await fixture.ApplyAsync(scope, token);
        }, cancellationToken: ownerDeadline.Token);
        await started.Task.WaitAsync(ownerDeadline.Token);
        var second = fixture.ExecuteAsync(provider, key, cancellationToken: waiting.Token);
        try
        {
            await observer.SecondAttempt.Task.WaitAsync(ownerDeadline.Token); waiting.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        }
        finally { release.TrySetResult(); }
        var result = await first;
        Assert.Equal(result, await fixture.ExecuteAsync(provider, key));
        Assert.Equal(1, fixture.Calls); Assert.Equal(1, await fixture.RecordsAsync());
    }

    [Fact]
    public async Task OwnerSubjectAndActorKindPartitionTheSameKeyAndBodyCannotChooseIdentity()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid();
        var body = OperationValue.Object(new("owner", OperationValue.Text("Releases")), new("subjectId", OperationValue.Identifier(Guid.NewGuid())));
        await using (var provider = fixture.Provider())
            foreach (var owner in Enum.GetValues<ModuleOwner>()) await fixture.ExecuteAsync(provider, key, body, owner);
        foreach (var schema in new[] { "iam", "rel", "pkg", "ins", "tsk", "aud" }) Assert.Equal(1, await fixture.RecordsAsync(schema));
        var subject = fixture.Actor.ActorId!.Value;
        fixture.Actor = new(ActorKind.ManagementSystem, subject);
        await using (var provider = fixture.Provider()) await fixture.ExecuteAsync(provider, key, body);
        fixture.Actor = new(ActorKind.Human, Guid.NewGuid());
        await using (var provider = fixture.Provider()) await fixture.ExecuteAsync(provider, key, body);
        Assert.Equal(3, await fixture.RecordsAsync()); Assert.Equal(8, fixture.Calls);
    }

    [Fact]
    public async Task HandlerFailureAndCancellationRollBackTheKeyBusinessAndAuditTogether()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid();
        await using var provider = fixture.Provider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ExecuteAsync(provider, key, action: async (scope, token) =>
        {
            await fixture.ApplyAsync(scope, token); throw new InvalidOperationException("Controlled Handler failure.");
        }));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExecuteAsync(provider, key, action: async (scope, token) =>
        {
            var result = await fixture.ApplyAsync(scope, token); cancellation.Cancel(); return result;
        }, cancellationToken: cancellation.Token));
        Assert.Equal(0, await fixture.RecordsAsync()); Assert.Equal(0, await fixture.AuditsAsync()); Assert.Equal(0, await fixture.ProbeCountAsync());
        var successful = await fixture.ExecuteAsync(provider, key);
        Assert.Equal(OperationStatus.Completed, successful.Status); Assert.Equal(1, await fixture.RecordsAsync());
        Assert.Equal(1, await fixture.AuditsAsync()); Assert.Equal(1, await fixture.ProbeCountAsync());
    }

    [Fact]
    public async Task NecessaryAuditFailureRollsBackAResultThatWasAlreadyFinalizedInTheTransaction()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid();
        await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, $"""
            CREATE FUNCTION aud.reject_idempotency_audit() RETURNS trigger LANGUAGE plpgsql AS $test$
            BEGIN IF NEW."Operation"='{fixture.Operation}' THEN RAISE EXCEPTION 'Controlled audit failure'; END IF; RETURN NEW; END; $test$;
            CREATE TRIGGER reject_idempotency_audit BEFORE INSERT ON aud.events FOR EACH ROW EXECUTE FUNCTION aud.reject_idempotency_audit();
            """);
        try
        {
            await using var provider = fixture.Provider();
            Assert.Equal(PersistenceFailure.DependencyUnavailable,
                (await Assert.ThrowsAsync<PersistenceException>(() => fixture.ExecuteAsync(provider, key))).Failure);
            Assert.Equal(0, await fixture.RecordsAsync()); Assert.Equal(0, await fixture.AuditsAsync()); Assert.Equal(0, await fixture.ProbeCountAsync());
        }
        finally { await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, "DROP TRIGGER reject_idempotency_audit ON aud.events; DROP FUNCTION aud.reject_idempotency_audit();"); }
        await using var retryProvider = fixture.Provider();
        await fixture.ExecuteAsync(retryProvider, key); Assert.Equal(1, await fixture.RecordsAsync());
    }

    [Fact]
    public async Task LostAcknowledgementIsResolvedInANewScopeWithoutRepeatingCommittedWork()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid(); var lost = new CommitFault(afterCommit: true);
        await using var provider = fixture.Provider(lost);
        var result = await fixture.ExecuteAsync(provider, key);
        Assert.Equal(result, await fixture.ExecuteAsync(provider, key));
        Assert.Equal(1, fixture.Calls); Assert.Equal(1, lost.Faults);
        Assert.Equal(1, await fixture.RecordsAsync()); Assert.Equal(1, await fixture.AuditsAsync()); Assert.Equal(1, await fixture.ProbeCountAsync());
    }

    [Fact]
    public async Task MissingResultOrUnavailableRecoveryKeepsTheOriginalUnknownOutcome()
    {
        var absent = new IdempotencyFixture(database); var absentKey = Guid.NewGuid(); var before = new CommitFault(afterCommit: false);
        await using (var provider = absent.Provider(before))
        {
            var failure = await Assert.ThrowsAsync<PersistenceException>(() => absent.ExecuteAsync(provider, absentKey));
            Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, failure.Failure); Assert.NotNull(failure.OperationId);
            Assert.Equal(1, absent.Calls); Assert.Equal(0, await absent.RecordsAsync()); Assert.Equal(0, await absent.AuditsAsync());
            await absent.ExecuteAsync(provider, absentKey); // A new caller retry, not an automatic replay.
            Assert.Equal(2, absent.Calls); Assert.Equal(1, await absent.RecordsAsync());
        }
        var unavailable = new IdempotencyFixture(database); var key = Guid.NewGuid(); var after = new CommitFault(afterCommit: true);
        var lookup = new RecoveryLookupFault(after);
        await using (var provider = unavailable.Provider(after, lookup))
        {
            var failure = await Assert.ThrowsAsync<PersistenceException>(() => unavailable.ExecuteAsync(provider, key));
            Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, failure.Failure); Assert.Equal(1, unavailable.Calls);
            Assert.Equal(1, await unavailable.RecordsAsync());
            lookup.Enabled = false;
            var result = await unavailable.ExecuteAsync(provider, key);
            Assert.Equal(failure.OperationId, result.OperationId); Assert.Equal(1, unavailable.Calls);
        }
    }

    [Fact]
    public async Task RecoveryReturnsTheCommittedWinnerWhenAnotherCallerRetriesAfterTheOriginalRollback()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid();
        var before = new CommitFault(afterCommit: false);
        OperationResultReference? uncommitted = null, winner = null;
        var replacement = new RecoveryRetry(before, async () =>
        {
            await using var competingProvider = fixture.Provider();
            winner = await fixture.ExecuteAsync(competingProvider, key);
        });
        await using var provider = fixture.Provider(before, replacement);
        var recovered = await fixture.ExecuteAsync(provider, key, action: async (scope, token) =>
            uncommitted = await fixture.ApplyAsync(scope, token));
        Assert.Equal(winner, recovered); Assert.NotEqual(uncommitted!.OperationId, recovered.OperationId);
        Assert.Equal(2, fixture.Calls); Assert.Equal(1, before.Faults);
        Assert.Equal(1, await fixture.RecordsAsync()); Assert.Equal(1, await fixture.AuditsAsync()); Assert.Equal(1, await fixture.ProbeCountAsync());
    }

    [Fact]
    public async Task RecoveryRechecksCurrentPermissionAndDoesNotCopyTheOldActorIntoTheNewScope()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid();
        var lost = new CommitFault(afterCommit: true, changed: () => fixture.Allowed = false);
        await using (var provider = fixture.Provider(lost))
        {
            var failure = await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.ExecuteAsync(provider, key));
            Assert.Equal(RequestFailure.PermissionDenied, failure.Failure); Assert.Equal(1, fixture.Calls);
            fixture.Allowed = true; await fixture.ExecuteAsync(provider, key); Assert.Equal(1, fixture.Calls);
        }
        var changedActor = new IdempotencyFixture(database); var anotherKey = Guid.NewGuid();
        var changed = new CommitFault(afterCommit: true, changed: () => changedActor.Actor = new(ActorKind.Human, Guid.NewGuid()));
        await using var anotherProvider = changedActor.Provider(changed);
        var mismatch = await Assert.ThrowsAsync<RequestRejectedException>(() => changedActor.ExecuteAsync(anotherProvider, anotherKey));
        Assert.Equal(RequestFailure.PermissionDenied, mismatch.Failure); Assert.Equal(1, changedActor.Calls);
    }

    [Fact]
    public async Task CompletedKeysCannotBeDeletedOrRewrittenAndUnfinalizedClaimsCannotCommit()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid();
        await using var provider = fixture.Provider(); await fixture.ExecuteAsync(provider, key);
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(database.WriterConnection,
            $"DELETE FROM iam.operation_results WHERE \"Operation\"='{fixture.Operation}'"));
        Assert.Equal("42501", deletion.SqlState);
        var rewrite = await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(database.WriterConnection,
            $"UPDATE iam.operation_results SET \"Status\"=2 WHERE \"Operation\"='{fixture.Operation}'"));
        Assert.Equal("23514", rewrite.SqlState);
        var incomplete = await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(database.WriterConnection, $"""
            INSERT INTO iam.operation_results ("ActorKind","SubjectId","Operation","IdempotencyKey","RequestDigest","OperationId")
            VALUES (2,'{fixture.Actor.ActorId}','{fixture.Operation}','{Guid.NewGuid()}','{new string('a',64)}','{Guid.NewGuid()}')
            """));
        Assert.Equal("23514", incomplete.SqlState); Assert.Equal(1, await fixture.RecordsAsync());
        await using var scope = provider.CreateAsyncScope();
        var unbound = scope.ServiceProvider.GetRequiredService<IOperationResultStore>();
        Assert.Equal(PersistenceFailure.ConfigurationInvalid, (await Assert.ThrowsAsync<PersistenceException>(() => unbound.FindAsync(default))).Failure);
    }

    [Fact]
    public async Task SensitiveInputAffectsOnlyTheDigestAndDoesNotAppearInStoredRowsOrDiagnostics()
    {
        var fixture = new IdempotencyFixture(database); var key = Guid.NewGuid(); var secret = Guid.NewGuid().ToString("N");
        var value = OperationValue.Text(secret); var field = new OperationField("inputOnly", value);
        var body = OperationValue.Object(field);
        var data = new OperationRequestData(key, OperationValue.Object(), body);
        Assert.DoesNotContain(secret, value.ToString()); Assert.DoesNotContain(secret, field.ToString()); Assert.DoesNotContain(secret, data.ToString());
        await using var provider = fixture.Provider(); await fixture.ExecuteAsync(provider, key, body);
        var row = await PersistenceDatabase.ScalarAsync<string>(database.ReaderConnection,
            "SELECT row_to_json(r)::text FROM iam.operation_results r WHERE \"Operation\"=@operation", new NpgsqlParameter("operation", fixture.Operation));
        Assert.DoesNotContain(secret, row); Assert.DoesNotContain("inputOnly", row);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.ExecuteAsync(provider, key,
            OperationValue.Object(new OperationField("inputOnly", OperationValue.Text(secret + "changed")))));
        Assert.DoesNotContain(secret, error.ToString()); Assert.Null(error.InnerException);
    }

    private sealed class AcquisitionObserver : DbCommandInterceptor
    {
        private int _calls;
        internal readonly TaskCompletionSource SecondAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ON CONFLICT (\"ActorKind\"", StringComparison.Ordinal) && Interlocked.Increment(ref _calls) == 2)
                SecondAttempt.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CommitFault(bool afterCommit, Action? changed = null) : DbTransactionInterceptor
    {
        internal int Faults;
        private void FailOnce() { if (Interlocked.CompareExchange(ref Faults, 1, 0) == 0) { changed?.Invoke(); throw new IOException("Controlled commit acknowledgement failure."); } }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { if (afterCommit) FailOnce(); return Task.CompletedTask; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { if (!afterCommit) FailOnce(); return ValueTask.FromResult(result); }
    }
    private sealed class RecoveryLookupFault(CommitFault commit) : DbCommandInterceptor
    {
        internal bool Enabled = true;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && commit.Faults == 1 && command.CommandText.Contains("operation_results", StringComparison.Ordinal))
                throw new IOException("Controlled recovery lookup failure.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class RecoveryRetry(CommitFault commit, Func<Task> retry) : DbCommandInterceptor
    {
        private int _started;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (commit.Faults == 1 && command.CommandText.Contains("operation_results", StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref _started, 1, 0) == 0)
                await retry(); // An explicit competing caller commits before the original request's recovery lookup.
            return result;
        }
    }
}
