using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Svm.AuditService;
using Svm.EntityFrameworkCore;
using Svm.EntityFrameworkCore.Framework;
using Svm.EntityFrameworkCore.Migrations;
using Svm.EventBus;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;
using Svm.Services.CrossCutting.Consumption;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.FrameworkTests;

internal sealed class ConsumptionFixture(string writer, string reader, MessagingOptions options, ConsumptionTestState? state = null)
{
    internal ConsumptionTestState State { get; } = state ?? new();
    internal static IntegrationConsumerBinding Binding => new(typeof(TaskPreparationAvailableV1), ConsumptionTestTypes.Handler);
    internal static TaskPreparationAvailableV1 Message(MessagingOptions options) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(),
        null, options.SiteId, Guid.NewGuid(), TaskPreparationWorkKind.TargetSelection, Guid.NewGuid(), 1);

    internal static async Task PrepareAsync(PersistenceDatabase db, bool permissions = true)
    {
        if (permissions) await new MigrationRunner(MigrationConfiguration.Create(db.MigrationConnection, db.WriterRole, db.ReaderRole, true)).ApplyAsync(default);
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, """
            CREATE TABLE tsk.consumer_probe (work_id uuid PRIMARY KEY, site_id uuid NOT NULL, software_id uuid NOT NULL,
                generation bigint NOT NULL, event_id uuid NOT NULL, kind integer NOT NULL, allowed boolean NOT NULL DEFAULT true,
                accepted bigint NOT NULL DEFAULT 0, effects integer NOT NULL DEFAULT 0);
            CREATE TABLE tsk.consumer_dispatch (event_id uuid PRIMARY KEY, work_id uuid NOT NULL, generation bigint NOT NULL);
            """);
    }
    internal static async Task RegisterWorkAsync(PersistenceDatabase db, TaskPreparationAvailableV1 message, bool advance = false)
    {
        await using var connection = new NpgsqlConnection(db.WriterConnection); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(advance ? """
            UPDATE tsk.consumer_probe SET generation=@generation,event_id=@event WHERE work_id=@work;
            INSERT INTO tsk.consumer_dispatch VALUES(@event,@work,@generation)
            """ : """
            INSERT INTO tsk.consumer_probe(work_id,site_id,software_id,generation,event_id,kind) VALUES(@work,@site,@software,@generation,@event,@kind);
            INSERT INTO tsk.consumer_dispatch VALUES(@event,@work,@generation)
            """, connection);
        command.Parameters.AddWithValue("work", message.WorkId); command.Parameters.AddWithValue("site", message.SiteId);
        command.Parameters.AddWithValue("software", message.SoftwareId); command.Parameters.AddWithValue("generation", message.DispatchSequence);
        command.Parameters.AddWithValue("event", message.EventId); command.Parameters.AddWithValue("kind", (int)message.WorkKind);
        await command.ExecuteNonQueryAsync();
    }
    internal IHost Host(params IInterceptor[] interceptors)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        var services = builder.Services;
        services.AddSvmRequestPipeline([], [DomainEventFixture.Binding()]).AddSvmPostgres(writer).AddSvmAudit();
        services.AddSingleton(State); services.AddSingleton(new DomainEventTestState());
        var dbOptions = new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(writer).AddInterceptors(interceptors)
            .ReplaceService<IModelCustomizer, DomainEventTestModelCustomizer>().Options;
        services.Replace(ServiceDescriptor.Scoped(_ => new SvmDbContext(dbOptions)));
        services.AddScoped<IIntegrationWorkAuthorizer, ConsumptionTestAuthorizer>();
        services.AddScoped(s => new ConsumptionEffects(s.GetRequiredService<SvmDbContext>(), s.GetRequiredService<IUnitOfWork>(),
            s.GetRequiredService<IAuditWriter>(), s.GetRequiredService<IIntegrationEventOutbox>(), State));
        services.AddSvmConsumption([Binding]); services.AddSvmMessaging(options, true, [Binding]);
        services.ValidateSvmFoundation();
        return builder.Build();
    }
    internal Task<long> EffectsAsync() => PersistenceDatabase.ScalarAsync<long>(reader, "SELECT COALESCE(sum(effects),0)::bigint FROM tsk.consumer_probe");
    internal Task<long> ResultsAsync() => PersistenceDatabase.ScalarAsync<long>(reader, "SELECT count(*) FROM tsk.operation_results");
    internal Task<long> AuditsAsync() => PersistenceDatabase.ScalarAsync<long>(reader, "SELECT count(*) FROM aud.events WHERE \"Operation\"='fixture.consume'");
    internal Task<long> ConsumedAsync() => PersistenceDatabase.ScalarAsync<long>(reader, "SELECT count(*) FROM framework.\"InboxState\" WHERE \"Consumed\" IS NOT NULL");

    // External test producer. No production Command or HTTP endpoint is introduced.
    internal static async Task SendAsync(IHost host, TaskPreparationAvailableV1 message)
    {
        var endpoint = await host.Services.GetRequiredService<IBus>().GetSendEndpoint(new Uri("queue:svm.task-preparation.available.v1"));
        await endpoint.Send(message, send => send.MessageId = message.EventId);
    }
}

public sealed class ConsumptionTestState
{
    public Guid ServiceId { get; init; } = Guid.NewGuid();
    public int Calls;
    public int Preflights;
    public int TransactionAuthorizations;
    public string? Failure;
    public int TransientFailures;
    public bool Outgoing;
    public readonly ConcurrentBag<Guid> HandlerScopes = [];
    public readonly ConcurrentBag<Guid> AuthorizationScopes = [];
    public readonly ConcurrentBag<DomainEventTestAggregate> Aggregates = [];
    public readonly ConcurrentBag<TaskControlAvailableV1> Messages = [];
    public Func<CancellationToken, Task>? BeforeReturn;
    public Func<Task>? BeforeCommit;
    public string? Marker;
}

internal sealed class ConsumptionTestAuthorizer(SvmDbContext context, ConsumptionTestState state) : IIntegrationWorkAuthorizer
{
    private readonly Guid _instance = Guid.NewGuid();
    public async ValueTask<IntegrationWorkAuthority> AuthorizeAsync(IIntegrationEvent message, ModuleOwner owner, bool insideTransaction, CancellationToken cancellationToken)
    {
        state.AuthorizationScopes.Add(_instance);
        if (insideTransaction) Interlocked.Increment(ref state.TransactionAuthorizations); else Interlocked.Increment(ref state.Preflights);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT p.site_id,p.software_id,p.work_id,p.kind,p.generation,p.event_id,p.allowed,d.generation,d.event_id
            FROM tsk.consumer_probe p JOIN tsk.consumer_dispatch d ON d.work_id=p.work_id
            WHERE p.work_id=@work AND d.event_id=@event
            """ + (insideTransaction ? " FOR UPDATE OF p" : ""), (NpgsqlConnection)context.Database.GetDbConnection(),
            (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.AddWithValue("work", message.WorkId); command.Parameters.AddWithValue("event", message.EventId);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        if (!await rows.ReadAsync(cancellationToken)) throw new IntegrationConsumptionException(ConsumptionFailure.Conflict);
        if (!rows.GetBoolean(6)) throw new IntegrationConsumptionException(ConsumptionFailure.PermissionDenied);
        return new(new(ActorKind.Service, state.ServiceId, rows.GetGuid(1), workOwner: owner, workId: rows.GetGuid(2)), owner,
            rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetInt32(3), rows.GetInt64(4), rows.GetGuid(5), rows.GetInt64(7), rows.GetGuid(8));
    }
}

public sealed class ConsumptionEffects
{
    private readonly SvmDbContext _db;
    private readonly IUnitOfWork _unit;
    private readonly IAuditWriter _audit;
    private readonly IIntegrationEventOutbox _outbox;
    private readonly ConsumptionTestState _state;
    private readonly Guid _instance = Guid.NewGuid();
    internal ConsumptionEffects(SvmDbContext db, IUnitOfWork unit, IAuditWriter audit, IIntegrationEventOutbox outbox, ConsumptionTestState state)
    { _db = db; _unit = unit; _audit = audit; _outbox = outbox; _state = state; }
    public async Task<OperationResultReference> HandleAsync(TaskPreparationAvailableV1 message, IntegrationConsumptionContext context, CancellationToken token)
    {
        Interlocked.Increment(ref _state.Calls); _state.HandlerScopes.Add(_instance);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>_db.SaveChangesAsync(token));
        if (_state.Marker is { } marker) await File.WriteAllTextAsync(marker + ".entered", "entered", token);
        var count = await _unit.ExecuteAsync(message.EventId, nested => _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE tsk.consumer_probe SET accepted={message.DispatchSequence},effects=effects+1
            WHERE work_id={message.WorkId} AND accepted<{message.DispatchSequence}
            """, nested), token);
        if (count > 0)
        {
            var aggregate = DomainEventFixture.Aggregate(); aggregate.Value = 1; aggregate.Raise(DomainEventFixture.Event(1));
            _state.Aggregates.Add(aggregate); _db.Add(aggregate);
            _audit.Append(new(message.EventId, context.Actor.ActorId, "Service", null, null, "fixture.consume", message.WorkId,
                "Accepted", "Consumer foundation fixture", message.CorrelationId.ToString("N")));
            if (_state.Outgoing)
                for (var i = 0; i < 2; i++)
                {
                    var next = new TaskControlAvailableV1(Guid.NewGuid(), DateTimeOffset.UtcNow, message.CorrelationId, message.EventId,
                        message.SiteId, message.SoftwareId, TaskControlWorkKind.Reschedule, Guid.NewGuid(), 1);
                    _state.Messages.Add(next); await _outbox.EnqueueAsync(next, token);
                }
        }
        if (_state.BeforeReturn is not null) await _state.BeforeReturn(token);
        if (_state.Failure == "handler") throw new InvalidOperationException("Controlled handler failure.");
        if (_state.Failure == "cancel") throw new OperationCanceledException(token);
        if (_state.Failure == "result") return new(Guid.NewGuid(), OperationStatus.Completed);
        if (_state.Failure == "resultWork") return new(message.EventId,OperationStatus.Accepted,workId:Guid.NewGuid());
        if (_state.Calls <= _state.TransientFailures) throw new PersistenceException(PersistenceFailure.DependencyUnavailable,message.EventId,"40001");
        return new(message.EventId, OperationStatus.Accepted, workId: message.WorkId);
    }
}

public class ConsumptionTestHandler(ConsumptionEffects effects) : IIntegrationEventHandler<TaskPreparationAvailableV1>
{
    public Task<OperationResultReference> HandleAsync(TaskPreparationAvailableV1 message, IntegrationConsumptionContext context, CancellationToken cancellationToken) =>
        effects.HandleAsync(message, context, cancellationToken);
}

internal static class ConsumptionTestTypes
{
    internal static readonly Type Handler = DefineHandler();
    private static Type DefineHandler()
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new("Svm.Application"), AssemblyBuilderAccess.Run).DefineDynamicModule("ConsumerFixture");
        var type = module.DefineType("Fixture.ConsumePackageWork", TypeAttributes.Public | TypeAttributes.Sealed, typeof(ConsumptionTestHandler));
        var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(ConsumptionEffects)]);
        var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(ConsumptionTestHandler).GetConstructor([typeof(ConsumptionEffects)])!); il.Emit(OpCodes.Ret);
        return type.CreateType()!;
    }
}

internal sealed class ConsumptionSaveFault(ConsumptionTestState state) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (state.Calls > 0) throw new IOException("Controlled business save failure.");
        return ValueTask.FromResult(result);
    }
}

internal sealed class ConsumptionCommitHook(ConsumptionTestState state, bool loseConfirmation = false) : DbTransactionInterceptor
{
    private int _commits;
    private int _attempts;
    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction t, TransactionEventData e, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (state.Outgoing && e.Context is SvmDbContext db && db.ConsumerTransactions.OperationId is { } operation)
        {
            var messages=db.ChangeTracker.Entries<OutboxMessage>().Select(m=>m.Entity).ToArray();
            Assert.Equal(2,messages.Length); Assert.All(messages,m=>{Assert.Equal(operation,m.InboxMessageId);Assert.NotNull(m.InboxConsumerId);Assert.Null(m.OutboxId);});
        }
        if (state.Calls > 0 && state.BeforeCommit is not null && Interlocked.Increment(ref _attempts)==1) await state.BeforeCommit();
        return result;
    }
    public override async Task TransactionCommittedAsync(DbTransaction t, TransactionEndEventData e, CancellationToken cancellationToken = default)
    {
        if (state.Calls > 0 && Interlocked.Increment(ref _commits) == 1)
        {
            if (state.Marker is { } marker) await File.WriteAllTextAsync(marker + ".committed", "committed", cancellationToken);
            if (state.Failure == "afterCommit") await Task.Delay(Timeout.Infinite, cancellationToken);
            if (loseConfirmation) throw new IOException("Controlled commit confirmation loss.");
        }
    }
}

internal sealed class ConsumptionTechnicalFault(ConsumptionTestState state,bool newEvent) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e,InterceptionResult<int> result,CancellationToken token=default)
    {
        if(e.Context is SvmDbContext db && db.ConsumerTransactions.OperationId is not null && !db.ConsumerTransactions.BusinessActive)
        {
            var root=state.Aggregates.Single();
            if(newEvent)root.Raise(DomainEventFixture.Event(2));else root.Value++;
        }
        return ValueTask.FromResult(result);
    }
}
