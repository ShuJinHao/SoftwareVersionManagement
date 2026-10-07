using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class DomainEventPersistenceTests(PersistenceDatabase database) : IClassFixture<PersistenceDatabase>
{
    [Fact]
    public async Task RootDispatchesMultipleEventsAndSubscribersIncludingNewTrackedAggregates()
    {
        var fixture = new DomainEventFixture(database);
        var root = DomainEventFixture.Aggregate();
        var child = DomainEventFixture.Aggregate();
        var initial = DomainEventFixture.Event(1);
        var occurredAt = initial.OccurredAt;
        var id = initial.EventId;
        await using var provider = fixture.Provider(bindings: [DomainEventFixture.Binding(multiple: true),
            new(ModuleOwner.Identity, DomainEventTestTypes.OtherEvent, new Svm.Services.CrossCutting.DomainEvents.DomainEventHandlerBinding(DomainEventTestTypes.FollowUpHandler, 1))]);
        var result = await fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), action: async (scope, token) =>
        {
            var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            context.Add(root); root.Raise(initial);
            await unit.ExecuteAsync(unit.CurrentOperationId!.Value, nested =>
            {
                root.Raise(DomainEventFixture.Event(2));
                Assert.Empty(fixture.State.Calls);
                return Task.FromResult(0);
            }, token);
            Assert.Empty(fixture.State.Calls);
            fixture.State.OnHandle = (handler, domainEvent, handlerToken) =>
            {
                Assert.Equal(token, handlerToken);
                if (handler == "First") root.Value++;
                if (handler == "First" && DomainEventTestState.Sequence(domainEvent) == 1)
                    root.Raise(DomainEventFixture.Event(3, type: DomainEventTestTypes.OtherEvent));
                if (handler == "Second" && DomainEventTestState.Sequence(domainEvent) == 2)
                {
                    context.Add(child); child.Raise(DomainEventFixture.Event(4, type: DomainEventTestTypes.OtherEvent)); child.Value = 8;
                }
                // Processing never removes a pending event before the database commit.
                Assert.Contains(initial, root.DomainEvents);
                return Task.CompletedTask;
            };
            return await fixture.Idempotency.ApplyAsync(scope, token);
        });
        Assert.Equal(["First:1", "Second:1", "First:2", "Second:2", "FollowUp:3", "FollowUp:4"], fixture.State.Calls);
        Assert.Equal(id, initial.EventId); Assert.Equal(occurredAt, initial.OccurredAt);
        Assert.Empty(root.DomainEvents); Assert.Empty(child.DomainEvents);
        Assert.Equal(2, await ValueAsync(root)); Assert.Equal(8, await ValueAsync(child));
        Assert.Equal(OperationStatus.Completed, result.Status);
        await AssertEffectsAsync(fixture, 1);
        Assert.Equal(3, fixture.State.HandlerInstances.Distinct().Count());
    }

    [Fact]
    public async Task NoPendingEventsAllowAnEmptyDirectoryAndNormalCommit()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate(); root.Value = 9;
        await using var provider = fixture.Provider(bindings: []);
        await fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), action: async (scope, token) =>
        {
            scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root);
            return await fixture.Idempotency.ApplyAsync(scope, token);
        });
        Assert.Empty(fixture.State.Calls); Assert.Equal(9, await ValueAsync(root));
        await AssertEffectsAsync(fixture, 1);
    }

    [Theory]
    [InlineData("missing", DomainEventFailure.MissingHandler)]
    [InlineData("ownership", DomainEventFailure.InvalidOwnership)]
    [InlineData("duplicate", DomainEventFailure.DuplicateIdentifier)]
    public async Task InvalidEventsRollBackBusinessAuditAndIdempotencyTogether(string fault, DomainEventFailure expected)
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate(foreign: fault == "ownership");
        var other = DomainEventFixture.Aggregate(); var id = Guid.NewGuid();
        await using var provider = fixture.Provider(bindings: fault == "missing" ? [] : null, foreignAggregate: fault == "ownership");
        var failure = await Assert.ThrowsAsync<DomainEventDispatchException>(() => fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(),
            action: async (scope, token) =>
            {
                var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
                context.Add(root); root.Raise(DomainEventFixture.Event(1, id));
                if (fault == "duplicate") { context.Add(other); other.Raise(DomainEventFixture.Event(2, id)); }
                return await fixture.Idempotency.ApplyAsync(scope, token);
            }));
        Assert.Equal(expected, failure.Failure);
        Assert.Single(root.DomainEvents); Assert.Equal(0, await database.CountAsync(root.Id.Value));
        Assert.Equal(0, await database.CountAsync(other.Id.Value)); await AssertEffectsAsync(fixture, 0);
    }

    [Fact]
    public async Task RecursiveNewEventsStopAtTheConfiguredLimitAndRollback()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate();
        fixture.State.OnHandle = (_, e, _) => { root.Value++; root.Raise(DomainEventFixture.Event(DomainEventTestState.Sequence(e) + 1)); return Task.CompletedTask; };
        await using var provider = fixture.Provider(new DomainEventOptions(3));
        var failure = await Assert.ThrowsAsync<DomainEventDispatchException>(() => fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(),
            action: async (scope, token) =>
            {
                scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); root.Raise(DomainEventFixture.Event(1));
                return await fixture.Idempotency.ApplyAsync(scope, token);
            }));
        Assert.Equal(DomainEventFailure.ProcessingLimitExceeded, failure.Failure);
        Assert.Equal(3, fixture.State.Calls.Count); Assert.Equal(4, root.DomainEvents.Count);
        Assert.Equal(0, await database.CountAsync(root.Id.Value)); await AssertEffectsAsync(fixture, 0);
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public async Task DefaultProcessingLimitHasNoSilentTruncation(int count, bool commits)
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate();
        await using var provider = fixture.Provider();
        Task<OperationResultReference> Run() => fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), action: async (scope, token) =>
        {
            scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root);
            for (var i = 0; i < count; i++) root.Raise(DomainEventFixture.Event(i));
            return await fixture.Idempotency.ApplyAsync(scope, token);
        });
        if (commits) { await Run(); Assert.Empty(root.DomainEvents); Assert.Equal(count, fixture.State.Calls.Count); }
        else
        {
            Assert.Equal(DomainEventFailure.ProcessingLimitExceeded, (await Assert.ThrowsAsync<DomainEventDispatchException>(Run)).Failure);
            Assert.Equal(count, root.DomainEvents.Count);
        }
        Assert.Equal(commits ? 1 : 0, await database.CountAsync(root.Id.Value)); await AssertEffectsAsync(fixture, commits ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriberExceptionOrCancellationRollsBackEarlierEffects(bool cancel)
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate(); using var cancellation = new CancellationTokenSource();
        fixture.State.OnHandle = (handler, _, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            if (handler == "First")
            {
                root.Value = 17;
                if (cancel) cancellation.Cancel();
                return Task.CompletedTask;
            }
            throw new InvalidOperationException("Controlled second subscriber failure.");
        };
        await using var provider = fixture.Provider(bindings: [DomainEventFixture.Binding(multiple: true)]);
        Task<OperationResultReference> Run() => fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), action: async (scope, token) =>
        {
            scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); root.Raise(DomainEventFixture.Event(1));
            return await fixture.Idempotency.ApplyAsync(scope, token);
        }, cancellationToken: cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else await Assert.ThrowsAsync<InvalidOperationException>(Run);
        Assert.Equal(cancel ? 1 : 2, fixture.State.Calls.Count); Assert.Single(root.DomainEvents);
        Assert.Equal(0, await database.CountAsync(root.Id.Value)); await AssertEffectsAsync(fixture, 0);
    }

    [Fact]
    public async Task SameOperationNestedFromAHandlerIsStillDispatchedOnlyByTheRoot()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate();
        await using var provider = fixture.Provider(); await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>(); var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        fixture.State.OnHandle = async (_, domainEvent, token) =>
        {
            if (DomainEventTestState.Sequence(domainEvent) != 1) return;
            await unit.ExecuteAsync(unit.CurrentOperationId!.Value, _ =>
            {
                root.Raise(DomainEventFixture.Event(2)); Assert.Single(fixture.State.Calls); return Task.FromResult(0);
            }, token);
            Assert.Single(fixture.State.Calls);
        };
        await unit.ExecuteAsync(Guid.NewGuid(), _ => { context.Add(root); root.Raise(DomainEventFixture.Event(1)); return Task.FromResult(0); }, default);
        Assert.Equal(["First:1", "First:2"], fixture.State.Calls); Assert.Empty(root.DomainEvents);
        Assert.Equal(1, await database.CountAsync(root.Id.Value));
    }

    [Fact]
    public async Task AHandlerCannotHideAFailedNestedOperationAndCommit()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate();
        await using var provider = fixture.Provider();
        var failure = await Assert.ThrowsAsync<PersistenceException>(() => fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), action: async (scope, token) =>
        {
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); root.Raise(DomainEventFixture.Event(1));
            fixture.State.OnHandle = async (_, _, handlerToken) =>
            {
                try { await unit.ExecuteAsync<int>(unit.CurrentOperationId!.Value, _ => throw new InvalidOperationException("Controlled nested failure."), handlerToken); }
                catch (InvalidOperationException) { }
            };
            return await fixture.Idempotency.ApplyAsync(scope, token);
        }));
        Assert.Equal(PersistenceFailure.OperationAborted, failure.Failure); Assert.Single(root.DomainEvents);
        Assert.Equal(0, await database.CountAsync(root.Id.Value)); await AssertEffectsAsync(fixture, 0);
    }

    [Fact]
    public async Task UnchangedAndDeletedTrackedRootsAlsoDispatchAndAcknowledge()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate();
        await using var provider = fixture.Provider();
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), _ =>
            { scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); return Task.FromResult(0); }, default);
        fixture.State.OnHandle = (_, _, _) => { root.Value++; return Task.CompletedTask; };
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), _ =>
            { context.Attach(root); Assert.Equal(EntityState.Unchanged, context.Entry(root).State); root.Raise(DomainEventFixture.Event(1)); return Task.FromResult(0); }, default);
        }
        Assert.Empty(root.DomainEvents); Assert.Equal(1, await ValueAsync(root));
        fixture.State.OnHandle = null;
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), _ =>
            { scope.ServiceProvider.GetRequiredService<SvmDbContext>().Remove(root); root.Raise(DomainEventFixture.Event(2)); return Task.FromResult(0); }, default);
        Assert.Equal(["First:1", "First:2"], fixture.State.Calls); Assert.Empty(root.DomainEvents);
        Assert.Equal(2, fixture.State.HandlerInstances.Distinct().Count());
        Assert.Equal(0, await database.CountAsync(root.Id.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCommitNeverAcknowledgesOrAutomaticallyRedispatches(bool committed)
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate(); var fault = new LostConfirmation(committed);
        await using var provider = fixture.Provider(interceptors: [fault]); await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>(); var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var failure = await Assert.ThrowsAsync<PersistenceException>(() => unit.ExecuteAsync(Guid.NewGuid(), _ =>
        { context.Add(root); root.Raise(DomainEventFixture.Event(1)); return Task.FromResult(0); }, default));
        Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, failure.Failure); Assert.Single(root.DomainEvents); Assert.Single(fixture.State.Calls);
        Assert.Equal(committed ? 1 : 0, await database.CountAsync(root.Id.Value)); Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(PersistenceFailure.ScopeCompleted, (await Assert.ThrowsAsync<PersistenceException>(() =>
            unit.ExecuteAsync(Guid.NewGuid(), _ => Task.FromResult(0), default))).Failure);
        Assert.Single(fixture.State.Calls);
    }

    [Fact]
    public async Task IdempotencyRecoveryUsesANewScopeWithoutAcknowledgingOrRepeatingEvents()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate(); var key = Guid.NewGuid();
        await using var provider = fixture.Provider(interceptors: [new LostConfirmation(committed: true)]);
        var result = await fixture.Idempotency.ExecuteAsync(provider, key, action: async (scope, token) =>
        {
            scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); root.Raise(DomainEventFixture.Event(1));
            return await fixture.Idempotency.ApplyAsync(scope, token);
        });
        Assert.Equal(result, await fixture.Idempotency.ExecuteAsync(provider, key));
        Assert.Equal(1, fixture.Idempotency.Calls); Assert.Single(fixture.State.Calls); Assert.Single(root.DomainEvents);
        Assert.Equal(1, await database.CountAsync(root.Id.Value)); await AssertEffectsAsync(fixture, 1);
    }

    [Fact]
    public async Task RecoveryPermissionRevocationDoesNotReexecuteCommittedHandlers()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate(); var key = Guid.NewGuid();
        await using var provider = fixture.Provider(interceptors: [new LostConfirmation(true, () => fixture.Idempotency.Allowed = false)]);
        Assert.Equal(RequestFailure.PermissionDenied, (await Assert.ThrowsAsync<RequestRejectedException>(() => fixture.Idempotency.ExecuteAsync(provider, key,
            action: async (scope, token) =>
            {
                scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); root.Raise(DomainEventFixture.Event(1));
                return await fixture.Idempotency.ApplyAsync(scope, token);
            }))).Failure);
        fixture.Idempotency.Allowed = true; await fixture.Idempotency.ExecuteAsync(provider, key);
        Assert.Single(fixture.State.Calls); Assert.Single(root.DomainEvents); Assert.Equal(1, fixture.Idempotency.Calls);
        await AssertEffectsAsync(fixture, 1);
    }

    [Fact]
    public async Task DetachingAnEventBearingRootDuringDispatchCannotDiscardItsChanges()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate();
        await using var provider = fixture.Provider();
        var failure = await Assert.ThrowsAsync<DomainEventDispatchException>(() => fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), action: async (scope, token) =>
        {
            var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
            context.Add(root); root.Raise(DomainEventFixture.Event(1));
            fixture.State.OnHandle = (_, _, _) => { context.Entry(root).State = EntityState.Detached; return Task.CompletedTask; };
            return await fixture.Idempotency.ApplyAsync(scope, token);
        }));
        Assert.Equal(DomainEventFailure.PendingEventsChanged, failure.Failure); Assert.Single(root.DomainEvents);
        Assert.Equal(0, await database.CountAsync(root.Id.Value)); await AssertEffectsAsync(fixture, 0);
    }

    [Theory]
    [InlineData("EventId")]
    [InlineData("OccurredAt")]
    public async Task EventIdentityAndTimeCannotChangeDuringHandling(string field)
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate();
        var domainEvent = DomainEventFixture.Event(1, type: DomainEventTestTypes.MutableEvent);
        fixture.State.OnHandle = (_, e, _) =>
        {
            e.GetType().GetProperty(field)!.SetValue(e, field == "EventId" ? Guid.NewGuid() : DateTimeOffset.UtcNow.AddSeconds(1));
            return Task.CompletedTask;
        };
        await using var provider = fixture.Provider(bindings: [new(ModuleOwner.Identity, DomainEventTestTypes.MutableEvent,
            new Svm.Services.CrossCutting.DomainEvents.DomainEventHandlerBinding(DomainEventTestTypes.MutableHandler, 1))]);
        var failure = await Assert.ThrowsAsync<DomainEventDispatchException>(() => fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), action: async (scope, token) =>
        {
            scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); root.Raise(domainEvent);
            return await fixture.Idempotency.ApplyAsync(scope, token);
        }));
        Assert.Equal(DomainEventFailure.InvalidEvent, failure.Failure); Assert.Single(root.DomainEvents); Assert.Single(fixture.State.Calls);
        Assert.Equal(0, await database.CountAsync(root.Id.Value)); await AssertEffectsAsync(fixture, 0);
    }

    [Fact]
    public async Task EventsCreatedDuringSaveCannotEscapeProcessingAndBeSilentlyCommitted()
    {
        var fixture = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate();
        await using var provider = fixture.Provider(interceptors: [new LateEvent(root)]);
        var failure = await Assert.ThrowsAsync<DomainEventDispatchException>(() => fixture.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), action: async (scope, token) =>
        {
            scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); root.Raise(DomainEventFixture.Event(1));
            return await fixture.Idempotency.ApplyAsync(scope, token);
        }));
        Assert.Equal(DomainEventFailure.PendingEventsChanged, failure.Failure); Assert.Equal(2, root.DomainEvents.Count); Assert.Single(fixture.State.Calls);
        Assert.Equal(0, await database.CountAsync(root.Id.Value)); await AssertEffectsAsync(fixture, 0);
    }

    private Task<int> ValueAsync(DomainEventTestAggregate root) => PersistenceDatabase.ScalarAsync<int>(database.ReaderConnection,
        "SELECT value FROM iam.foundation_probe WHERE id=@id", new NpgsqlParameter("id", root.Id.Value));

    private static async Task AssertEffectsAsync(DomainEventFixture fixture, long count)
    {
        Assert.Equal(count, await fixture.Idempotency.RecordsAsync()); Assert.Equal(count, await fixture.Idempotency.AuditsAsync());
        Assert.Equal(count, await fixture.Idempotency.ProbeCountAsync());
    }

    private sealed class LostConfirmation(bool committed, Action? changed = null) : DbTransactionInterceptor
    {
        private int _fault;
        private void FailOnce() { if (Interlocked.Exchange(ref _fault, 1) == 0) { changed?.Invoke(); throw new IOException("Controlled lost commit confirmation."); } }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { if (committed) FailOnce(); return Task.CompletedTask; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { if (!committed) FailOnce(); return ValueTask.FromResult(result); }
    }

    private sealed class LateEvent(DomainEventTestAggregate root) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        { root.Raise(DomainEventFixture.Event(2)); return ValueTask.FromResult(result); }
    }
}
