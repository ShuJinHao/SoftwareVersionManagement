using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Svm.EntityFrameworkCore;
using Svm.EntityFrameworkCore.Framework;
using Svm.EntityFrameworkCore.Messaging;
using Svm.EventBus;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class OutboxPersistenceTests : IAsyncLifetime
{
    private readonly PersistenceDatabase database = new();
    public Task InitializeAsync() => database.InitializeAsync();
    public Task DisposeAsync() => database.DisposeAsync();
    [Fact]
    public async Task OfflineProducerStagesMultipleMessagesAtomicallyAndNestedOperationDoesNotCommitEarly()
    {
        var fixture = new IdempotencyFixture(database); var options = OutboxFixture.Options();
        var first = OutboxFixture.Message(options); var second = OutboxFixture.Message(options);
        await using var provider = OutboxFixture.Provider(fixture, options);
        var result = await fixture.ExecuteAsync(provider, Guid.NewGuid(), owner: ModuleOwner.Packages, action: async (scope, token) =>
        {
            var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>(); var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();
            await outbox.EnqueueAsync(first, token);
            await unit.ExecuteAsync(unit.CurrentOperationId!.Value, async nested => { await outbox.EnqueueAsync(second, nested); return 0; }, token);
            Assert.Equal(0, await OutboxFixture.MessagesAsync(database));
            Assert.Equal(2, context.ChangeTracker.Entries<OutboxMessage>().Count());
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(token));
            return await fixture.ApplyAsync(scope, token);
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, await OutboxFixture.MessagesAsync(database)); Assert.Equal(1, await OutboxFixture.StatesAsync(database));
        await AssertEffectsAsync(fixture, 1);
        await using var check = provider.CreateAsyncScope(); var db = check.ServiceProvider.GetRequiredService<SvmDbContext>();
        var stored = await db.Set<OutboxMessage>().OrderBy(m => m.SequenceNumber).ToListAsync();
        Assert.Equal([first.EventId, second.EventId], stored.Select(m => m.MessageId));
        Assert.All(stored, m => { Assert.Contains("svm.package-work.available.v1", m.Body); Assert.DoesNotContain(options.Password, m.Body); Assert.Null(m.InboxMessageId); });
        Assert.Equal(first.CorrelationId, stored[0].CorrelationId); Assert.Equal(OperationStatus.Completed, result.Status);
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("cancel")]
    [InlineData("save")]
    [InlineData("audit")]
    [InlineData("result")]
    public async Task FailuresRollBackBusinessAuditResultAndOutbox(string stage)
    {
        var fixture = new IdempotencyFixture(database); var options = OutboxFixture.Options(); using var cancellation = new CancellationTokenSource();
        await using var provider = OutboxFixture.Provider(fixture, options, stage == "save" ? [new SaveFault()] : []);
        if (stage == "audit") await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, """
            CREATE FUNCTION aud.outbox_test_fail() RETURNS trigger LANGUAGE plpgsql AS $test$ BEGIN RAISE EXCEPTION 'Controlled audit failure'; END; $test$;
            CREATE TRIGGER outbox_test_fail BEFORE INSERT ON aud.events FOR EACH ROW EXECUTE FUNCTION aud.outbox_test_fail();
            """);
        try
        {
            async Task Run() => await fixture.ExecuteAsync(provider, Guid.NewGuid(), owner: ModuleOwner.Packages, action: async (scope, token) =>
            {
                await scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>().EnqueueAsync(OutboxFixture.Message(options), token);
                var result = await fixture.ApplyAsync(scope, token);
                if (stage == "handler") throw new InvalidOperationException("Controlled handler failure.");
                if (stage == "cancel") cancellation.Cancel();
                if (stage == "result") return new OperationResultReference(Guid.NewGuid(), OperationStatus.Completed);
                return result;
            }, cancellationToken: cancellation.Token);
            await Assert.ThrowsAnyAsync<Exception>(Run);
            await AssertEffectsAsync(fixture, 0); Assert.Equal(0, await OutboxFixture.MessagesAsync(database)); Assert.Equal(0, await OutboxFixture.StatesAsync(database));
        }
        finally { if (stage == "audit") await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, "DROP TRIGGER outbox_test_fail ON aud.events; DROP FUNCTION aud.outbox_test_fail();"); }
    }

    [Fact]
    public async Task MissingDomainHandlerRollsBackAlreadyStagedOutboxAndKeepsTheEvent()
    {
        var domain = new DomainEventFixture(database); var root = DomainEventFixture.Aggregate(); var options = OutboxFixture.Options();
        await using var provider = domain.Provider(bindings: [], configure: s => s.AddSvmMessaging(options, false));
        await Assert.ThrowsAsync<DomainEventDispatchException>(() => domain.Idempotency.ExecuteAsync(provider, Guid.NewGuid(), owner: ModuleOwner.Packages,
            action: async (scope, token) =>
            {
                scope.ServiceProvider.GetRequiredService<SvmDbContext>().Add(root); root.Raise(DomainEventFixture.Event(1));
                await scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>().EnqueueAsync(OutboxFixture.Message(options), token);
                return await domain.Idempotency.ApplyAsync(scope, token);
            }));
        Assert.Single(root.DomainEvents); Assert.Equal(0, await OutboxFixture.MessagesAsync(database)); await AssertEffectsAsync(domain.Idempotency, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCommitIsOnlyLookedUpAndNeverReenqueued(bool committed)
    {
        var fixture = new IdempotencyFixture(database); var options = OutboxFixture.Options(); var key = Guid.NewGuid(); var message = OutboxFixture.Message(options);
        await using var provider = OutboxFixture.Provider(fixture, options, new CommitFault(committed));
        if (committed)
        {
            var result = await OutboxFixture.StageAsync(fixture, provider, [message], key);
            Assert.Equal(result, await OutboxFixture.StageAsync(fixture, provider, [message], key));
            Assert.Equal(1, await OutboxFixture.MessagesAsync(database)); await AssertEffectsAsync(fixture, 1);
        }
        else
        {
            Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, (await Assert.ThrowsAsync<PersistenceException>(() => OutboxFixture.StageAsync(fixture, provider, [message], key))).Failure);
            Assert.Equal(0, await OutboxFixture.MessagesAsync(database)); await AssertEffectsAsync(fixture, 0);
        }
        Assert.Equal(1, fixture.Calls);
    }

    [Theory]
    [InlineData("owner", OutboxFailure.InvalidOwnership)]
    [InlineData("type", OutboxFailure.InvalidMessage)]
    [InlineData("site", OutboxFailure.InvalidMessage)]
    [InlineData("workKind", OutboxFailure.InvalidMessage)]
    [InlineData("utc", OutboxFailure.InvalidMessage)]
    public async Task InvalidOwnershipAndPayloadCannotCreateSendingIntent(string fault, OutboxFailure failure)
    {
        var fixture = new IdempotencyFixture(database); var options = OutboxFixture.Options();
        IIntegrationEvent message = OutboxFixture.Message(options);
        if (fault == "type") message = new UnknownMessage();
        if (fault == "site") message = ((PackageWorkAvailableV1)message) with { SiteId = Guid.NewGuid() };
        if (fault == "workKind") message = ((PackageWorkAvailableV1)message) with { WorkKind = (PackageWorkKind)99 };
        if (fault == "utc") message = ((PackageWorkAvailableV1)message) with { OccurredAt = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8)) };
        await using var provider = OutboxFixture.Provider(fixture, options);
        var error = await Assert.ThrowsAsync<OutboxException>(() => fixture.ExecuteAsync(provider, Guid.NewGuid(), owner: fault == "owner" ? ModuleOwner.Identity : ModuleOwner.Packages,
            action: async (scope, token) => { await scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>().EnqueueAsync(message, token); return await fixture.ApplyAsync(scope, token); }));
        Assert.Equal(failure, error.Failure); Assert.Equal(0, await OutboxFixture.MessagesAsync(database)); Assert.Equal(0, fixture.Calls);
    }

    [Fact]
    public async Task TransactionRequiredAndNativeDeliveryModelCannotWriteModuleEntities()
    {
        var fixture = new IdempotencyFixture(database); var options = OutboxFixture.Options(); await using var provider = OutboxFixture.Provider(fixture, options);
        await using var scope = provider.CreateAsyncScope();
        Assert.Equal(OutboxFailure.TransactionRequired, (await Assert.ThrowsAsync<OutboxException>(() =>
            scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>().EnqueueAsync(OutboxFixture.Message(options), default))).Failure);
        var source = scope.ServiceProvider.GetRequiredService<WriteDataSource>();
        await using var delivery = new OutboxDeliveryContext(source);
        Assert.Equal(3, delivery.Model.GetEntityTypes().Count());
        Assert.Throws<InvalidOperationException>(() => delivery.Add(new Svm.Core.Identity.UserAccount(new(Guid.NewGuid()), "fixture", "Fixture", "fixture-hash")));
        var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
        var pendingModelChanges = context.Database.HasPendingModelChanges();
        if (pendingModelChanges)
        {
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
            var details = "Snapshot\n" + snapshot.ToDebugString(MetadataDebugStringOptions.LongDefault) +
                "\nCurrent\n" + context.GetService<IDesignTimeModel>().Model.ToDebugString(MetadataDebugStringOptions.LongDefault);
            await File.WriteAllTextAsync(Path.Combine(OutboxFixture.Root, "artifacts/outbox-model-diff.txt"), details);
        }
        Assert.False(pendingModelChanges);
        Assert.True(await PersistenceDatabase.ScalarAsync<bool>(database.WriterConnection,
            "SELECT has_table_privilege(current_user,'framework.\"OutboxMessage\"','DELETE') AND NOT has_table_privilege(current_user,'framework.\"InboxState\"','INSERT')"));
        Assert.True(await PersistenceDatabase.ScalarAsync<bool>(database.ReaderConnection,
            "SELECT NOT has_table_privilege(current_user,'framework.\"OutboxMessage\"','INSERT')"));
    }
    private async Task AssertEffectsAsync(IdempotencyFixture fixture, int count)
    { Assert.Equal(count, await fixture.RecordsAsync("pkg")); Assert.Equal(count, await fixture.AuditsAsync()); Assert.Equal(count, await fixture.ProbeCountAsync()); }
    [Fact]
    public async Task DeliveryCannotUseMigrationOwnerAndSaveHooksCannotEnqueueAfterTheOperationIsSealed()
    {
        var fixture = new IdempotencyFixture(database); var options = OutboxFixture.Options(); var late = new LateEnqueue();
        await using (var provider = OutboxFixture.Provider(fixture, options, late))
        {
            Assert.Equal(OutboxFailure.TransactionRequired, (await Assert.ThrowsAsync<OutboxException>(() => fixture.ExecuteAsync(provider, Guid.NewGuid(), owner: ModuleOwner.Packages,
                action: async (scope, token) =>
                {
                    var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();
                    await outbox.EnqueueAsync(OutboxFixture.Message(options), token);
                    late.Enqueue = t => outbox.EnqueueAsync(OutboxFixture.Message(options), t);
                    return await fixture.ApplyAsync(scope, token);
                }))).Failure);
            Assert.Equal(0, await OutboxFixture.MessagesAsync(database)); await AssertEffectsAsync(fixture, 0);
        }
        var services = new ServiceCollection(); services.AddSvmPostgres(database.MigrationConnection);
        await using var privileged = services.BuildServiceProvider(); await using var scope = privileged.CreateAsyncScope();
        await using var delivery = new OutboxDeliveryContext(scope.ServiceProvider.GetRequiredService<WriteDataSource>());
        delivery.Add(new OutboxState { OutboxId = Guid.NewGuid(), Created = DateTime.UtcNow });
        Assert.Equal(PersistenceFailure.ConfigurationInvalid, (await Assert.ThrowsAsync<PersistenceException>(() => delivery.SaveChangesAsync())).Failure);
        Assert.Equal(0, await OutboxFixture.StatesAsync(database));
    }
    private sealed class LateEnqueue : SaveChangesInterceptor
    {
        internal Func<CancellationToken,Task>? Enqueue;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Enqueue is not null) await Enqueue(cancellationToken); return result; }
    }
    private sealed class SaveFault : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new IOException("Controlled save failure.");
    }
    private sealed class CommitFault(bool after) : DbTransactionInterceptor
    {
        private int _faults;
        private void Fail() { if (Interlocked.CompareExchange(ref _faults, 1, 0) == 0) throw new IOException("Controlled commit acknowledgement loss."); }
        public override Task TransactionCommittedAsync(DbTransaction t, TransactionEndEventData e, CancellationToken cancellationToken = default)
        { if (after) Fail(); return Task.CompletedTask; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction t, TransactionEventData e, InterceptionResult result, CancellationToken cancellationToken = default)
        { if (!after) Fail(); return ValueTask.FromResult(result); }
    }
    private sealed record UnknownMessage : IIntegrationEvent
    {
        public Guid EventId => Guid.NewGuid(); public int SchemaVersion => 1; public string MessageType => "unknown";
        public DateTimeOffset OccurredAt => DateTimeOffset.UtcNow; public Guid CorrelationId => Guid.NewGuid(); public Guid? CausationId => null;
        public Guid SiteId => Guid.NewGuid(); public Guid SoftwareId => Guid.NewGuid(); public Guid WorkId => Guid.NewGuid(); public long DispatchSequence => 1;
    }
}
