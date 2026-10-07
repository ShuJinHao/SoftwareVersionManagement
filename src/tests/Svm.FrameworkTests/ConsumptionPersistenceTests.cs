using System.Text.Json.Nodes;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Svm.EntityFrameworkCore.Framework;
using Svm.EntityFrameworkCore.Migrations;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class ConsumptionPersistenceTests : IAsyncLifetime
{
    private readonly PersistenceDatabase _db = new();
    private readonly OutboxBroker _broker = new();
    public async Task InitializeAsync() { await _db.InitializeAsync(); await _broker.InitializeAsync(); await ConsumptionFixture.PrepareAsync(_db); }
    public async Task DisposeAsync() { try { await _broker.DisposeAsync(); } finally { await _db.DisposeAsync(); } }

    [Fact]
    public async Task BusinessAuditResultDomainEventsAndConsumerOutboxCommitTogether()
    {
        var fixture = new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options);
        fixture.State.Outgoing = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.BeforeReturn = async token => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10),token); };
        using var host = fixture.Host(new ConsumptionCommitHook(fixture.State)); await host.StartAsync();
        var message = ConsumptionFixture.Message(_broker.Options); await ConsumptionFixture.RegisterWorkAsync(_db,message);
        await ConsumptionFixture.SendAsync(host,message); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0,await fixture.EffectsAsync()); Assert.Equal(0,await fixture.AuditsAsync()); Assert.Equal(0,await fixture.ResultsAsync());
        Assert.Equal(0,await fixture.ConsumedAsync()); Assert.Equal(0,await OutboxFixture.MessagesAsync(_db));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.BeforeCommit = async () =>
        {
            Assert.Equal(0,await fixture.ConsumedAsync()); Assert.Single(fixture.State.Aggregates.Single().DomainEvents); observed.TrySetResult();
        };
        release.TrySetResult();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await OutboxFixture.WaitAsync(async () => await fixture.ConsumedAsync()==1 && await OutboxFixture.MessagesAsync(_db)==0);
        Assert.Equal(1,await fixture.EffectsAsync()); Assert.Equal(1,await fixture.ResultsAsync()); Assert.Equal(1,await fixture.AuditsAsync());
        Assert.Empty(fixture.State.Aggregates.Single().DomainEvents); Assert.Equal(1,fixture.State.Calls); Assert.Equal(1,fixture.State.TransactionAuthorizations);
        await OutboxFixture.WaitAsync(async () => await _broker.QueueCountAsync("svm.task-control.available.v1")>=2);
        var outgoing = await _broker.TakeAsync("svm.task-control.available.v1"); Assert.Equal(2,outgoing.Count);
        Assert.Equal(fixture.State.Messages.Select(m=>m.EventId).Order(),outgoing.Select(m=>System.Text.Json.JsonDocument.Parse(m.GetProperty("payload").GetString()!).RootElement.GetProperty("messageId").GetGuid()).Order());
        Assert.Equal(0,await OutboxFixture.StatesAsync(_db)); // Consumer association, not a second Bus Outbox.
        await host.StopAsync();
    }

    [Theory]
    [InlineData("handler")]
    [InlineData("cancel")]
    [InlineData("save")]
    [InlineData("audit")]
    [InlineData("result")]
    [InlineData("resultWork")]
    [InlineData("domain")]
    [InlineData("late-event")]
    [InlineData("technical-business")]
    public async Task AFailedConsumptionRollsBackAllAcceptedFacts(string failure)
    {
        var fixture = new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options);
        fixture.State.Failure=failure; fixture.State.Outgoing=true;
        if (failure=="audit") await PersistenceDatabase.ExecuteAsync(_db.MigrationConnection,"""
            CREATE FUNCTION aud.consumer_fail() RETURNS trigger LANGUAGE plpgsql AS $test$ BEGIN RAISE EXCEPTION 'Controlled audit failure'; END; $test$;
            CREATE TRIGGER consumer_fail BEFORE INSERT ON aud.events FOR EACH ROW EXECUTE FUNCTION aud.consumer_fail();
            """);
        if (failure=="domain") fixture.State.BeforeReturn=_ => { fixture.State.Aggregates.Single().Raise(DomainEventFixture.Event(2,type:DomainEventTestTypes.OtherEvent)); return Task.CompletedTask; };
        using var host = fixture.Host(failure switch { "save"=>[new ConsumptionSaveFault(fixture.State)],
            "late-event"=>[new ConsumptionTechnicalFault(fixture.State,true)],"technical-business"=>[new ConsumptionTechnicalFault(fixture.State,false)],_=>[] }); await host.StartAsync();
        var message=ConsumptionFixture.Message(_broker.Options); await ConsumptionFixture.RegisterWorkAsync(_db,message);
        await ConsumptionFixture.SendAsync(host,message);
        await OutboxFixture.WaitAsync(async()=>await _broker.QueueCountAsync("svm.task-preparation.available.v1_error")>=1);
        Assert.Equal(0,await fixture.EffectsAsync()); Assert.Equal(0,await fixture.ResultsAsync()); Assert.Equal(0,await fixture.AuditsAsync());
        Assert.Equal(0,await fixture.ConsumedAsync()); Assert.Equal(0,await OutboxFixture.MessagesAsync(_db)); Assert.Equal(0,await OutboxFixture.StatesAsync(_db));
        Assert.Equal(1,fixture.State.Calls); Assert.All(fixture.State.Aggregates,a=>Assert.NotEmpty(a.DomainEvents));
        // The native initial claim is allowed to survive; it is not acceptance.
        Assert.Equal(1,await PersistenceDatabase.ScalarAsync<long>(_db.ReaderConnection,"SELECT count(*) FROM framework.\"InboxState\" WHERE \"Consumed\" IS NULL"));
        await host.StopAsync();
    }

    [Fact]
    public async Task DuplicateDeliveryChecksCurrentPermissionBeforeTheInboxShortcut()
    {
        var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options);
        using var host=fixture.Host(); await host.StartAsync(); var message=ConsumptionFixture.Message(_broker.Options);
        await ConsumptionFixture.RegisterWorkAsync(_db,message); await ConsumptionFixture.SendAsync(host,message);
        await OutboxFixture.WaitAsync(async()=>await fixture.ConsumedAsync()==1);
        await ConsumptionFixture.SendAsync(host,message); await OutboxFixture.WaitAsync(()=>Task.FromResult(fixture.State.Preflights>=2));
        Assert.Equal(1,fixture.State.Calls); Assert.Equal(1,await fixture.EffectsAsync());
        await PersistenceDatabase.ExecuteAsync(_db.WriterConnection,"UPDATE tsk.consumer_probe SET allowed=false");
        await ConsumptionFixture.SendAsync(host,message);
        await OutboxFixture.WaitAsync(async()=>await _broker.QueueCountAsync("svm.task-preparation.available.v1_error")>=1);
        Assert.Equal(1,fixture.State.Calls); Assert.Equal(1,await fixture.ResultsAsync()); await host.StopAsync();
    }

    [Fact]
    public async Task CleanupAndOldGenerationsCannotErasePersistentDeduplication()
    {
        var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options with {InboxWindowMinutes=1});
        using var host=fixture.Host(); await host.StartAsync(); var old=ConsumptionFixture.Message(_broker.Options);
        await ConsumptionFixture.RegisterWorkAsync(_db,old); await ConsumptionFixture.SendAsync(host,old);
        await OutboxFixture.WaitAsync(async()=>await fixture.ConsumedAsync()==1);
        await OutboxFixture.WaitAsync(async()=>await PersistenceDatabase.ScalarAsync<long>(_db.ReaderConnection,"SELECT count(*) FROM framework.\"InboxState\" WHERE \"Delivered\" IS NOT NULL")==1);
        await PersistenceDatabase.ExecuteAsync(_db.WriterConnection,"UPDATE framework.\"InboxState\" SET \"Delivered\"=clock_timestamp()-interval '2 minutes'");
        await OutboxFixture.WaitAsync(async()=>await PersistenceDatabase.ScalarAsync<long>(_db.ReaderConnection,"SELECT count(*) FROM framework.\"InboxState\"")==0);
        await ConsumptionFixture.SendAsync(host,old); await OutboxFixture.WaitAsync(async()=>await fixture.ConsumedAsync()==1);
        Assert.Equal(1,fixture.State.Calls); Assert.Equal(1,await fixture.EffectsAsync()); Assert.Equal(1,await fixture.ResultsAsync());
        var next=old with {EventId=Guid.NewGuid(),DispatchSequence=2}; await ConsumptionFixture.RegisterWorkAsync(_db,next,true);
        await ConsumptionFixture.SendAsync(host,old); await ConsumptionFixture.SendAsync(host,next);
        await OutboxFixture.WaitAsync(async()=>await fixture.EffectsAsync()==2);
        Assert.Equal(2,fixture.State.Calls); Assert.Equal(2,await fixture.ResultsAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task CommitConfirmationLossVerifiesInANewScopeWithoutAcknowledgingOrRerunning()
    {
        var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options);
        fixture.State.Outgoing=true;
        using var host=fixture.Host(new ConsumptionCommitHook(fixture.State,loseConfirmation:true)); await host.StartAsync();
        var message=ConsumptionFixture.Message(_broker.Options); await ConsumptionFixture.RegisterWorkAsync(_db,message);
        await ConsumptionFixture.SendAsync(host,message);
        await OutboxFixture.WaitAsync(async()=>await _broker.QueueCountAsync("svm.task-preparation.available.v1_error")>=1);
        Assert.Equal(1,await fixture.EffectsAsync()); Assert.Equal(1,await fixture.ConsumedAsync()); Assert.Equal(1,fixture.State.Calls);
        Assert.Equal(2,await OutboxFixture.MessagesAsync(_db)); Assert.Single(fixture.State.Aggregates.Single().DomainEvents);
        Assert.Equal(2,fixture.State.AuthorizationScopes.Distinct().Count()); // original and verification scopes
        await ConsumptionFixture.SendAsync(host,message);
        await OutboxFixture.WaitAsync(async()=>await OutboxFixture.MessagesAsync(_db)==0);
        Assert.Equal(1,fixture.State.Calls); Assert.Single(fixture.State.Aggregates.Single().DomainEvents);
        Assert.Equal(2,(await _broker.TakeAsync("svm.task-control.available.v1")).Count); await host.StopAsync();
    }

    [Fact]
    public async Task UnknownContractIsQuarantinedOnceAndTheNextValidMessageStillCommits()
    {
        var fixture = new ConsumptionFixture(_db.WriterConnection, _db.ReaderConnection, _broker.Options);
        using var host = fixture.Host(); await host.StartAsync();
        var message = ConsumptionFixture.Message(_broker.Options);
        await ConsumptionFixture.RegisterWorkAsync(_db, message);
        var endpoint = await host.Services.GetRequiredService<IBus>().GetSendEndpoint(new Uri("queue:svm.raw-fixture.v1"));
        await endpoint.Send(message, send => send.MessageId = message.EventId);
        await OutboxFixture.WaitAsync(async () => await _broker.QueueCountAsync("svm.raw-fixture.v1") >= 1);
        var envelope = JsonNode.Parse((await _broker.TakeAsync("svm.raw-fixture.v1")).Single().GetProperty("payload").GetString()!)!;
        envelope["messageType"] = new JsonArray("urn:message:Unknown:UnknownV9");
        await _broker.PublishRawAsync("svm.task-preparation.available.v1", envelope.ToJsonString());
        await OutboxFixture.WaitAsync(async () => await _broker.QueueCountAsync("svm.task-preparation.available.v1_error") >= 1);
        Assert.Equal(0, fixture.State.Calls); Assert.Equal(0, await fixture.ConsumedAsync());
        Assert.Equal(0, await fixture.EffectsAsync()); Assert.Equal(0, await fixture.AuditsAsync()); Assert.Equal(0, await fixture.ResultsAsync());
        Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(_db.ReaderConnection, "SELECT count(*) FROM framework.\"InboxState\""));
        await ConsumptionFixture.SendAsync(host, message);
        await OutboxFixture.WaitAsync(async () => await fixture.ConsumedAsync() == 1);
        await host.StopAsync();
        Assert.Equal(1, fixture.State.Calls); Assert.Equal(1, await fixture.EffectsAsync());
        Assert.Equal(1, await _broker.QueueCountAsync("svm.task-preparation.available.v1_error"));
        Assert.Equal(0, await _broker.QueueCountAsync("svm.task-preparation.available.v1_skipped"));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("type")]
    [InlineData("messageId")]
    [InlineData("software")]
    [InlineData("kind")]
    [InlineData("bodyConflict")]
    [InlineData("site")]
    [InlineData("permissionHeader")]
    [InlineData("caseShadow")]
    public async Task RawInvalidFieldsAndChangedReplayBodyAreQuarantined(string fault)
    {
        var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options);
        using var host=fixture.Host(); await host.StartAsync(); var message=ConsumptionFixture.Message(_broker.Options);
        await ConsumptionFixture.RegisterWorkAsync(_db,message);
        // Materialize a real serializer envelope on an unconsumed owned queue, then alter only the tested raw field.
        var endpoint=await host.Services.GetRequiredService<IBus>().GetSendEndpoint(new Uri("queue:svm.raw-fixture.v1"));
        await endpoint.Send(message,s=>s.MessageId=message.EventId);
        await OutboxFixture.WaitAsync(async()=>await _broker.QueueCountAsync("svm.raw-fixture.v1")>=1);
        var envelope=JsonNode.Parse((await _broker.TakeAsync("svm.raw-fixture.v1")).Single().GetProperty("payload").GetString()!)!;
        var body=envelope["message"]!;
        switch(fault)
        {
            case "schema": body["schemaVersion"]=99; break;
            case "caseShadow": body["SchemaVersion"]=99; break;
            case "type": body["messageType"]="invalid.v9"; break;
            case "messageId": envelope["messageId"]=Guid.NewGuid(); break;
            case "software": body["softwareId"]=Guid.NewGuid(); break;
            case "site": body["siteId"]=Guid.NewGuid(); break;
            case "permissionHeader":
                await PersistenceDatabase.ExecuteAsync(_db.WriterConnection,"UPDATE tsk.consumer_probe SET allowed=false");
                envelope["headers"]=new JsonObject { ["permission"]="integration.receive",["actorId"]=Guid.NewGuid() };
                body["permission"]="integration.receive";break;
            case "kind": body["workKind"]=99; break;
            case "bodyConflict":
                await ConsumptionFixture.SendAsync(host,message); await OutboxFixture.WaitAsync(async()=>await fixture.ConsumedAsync()==1);
                body["correlationId"]=Guid.NewGuid(); break;
        }
        await _broker.PublishRawAsync("svm.task-preparation.available.v1",envelope.ToJsonString());
        await OutboxFixture.WaitAsync(async()=>await _broker.QueueCountAsync("svm.task-preparation.available.v1_error")>=1);
        Assert.Equal(fault=="bodyConflict"?1:0,await fixture.ConsumedAsync()); Assert.Equal(fault=="bodyConflict"?1:0,fixture.State.Calls);
        if(fault!="bodyConflict") Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(_db.ReaderConnection,"SELECT count(*) FROM framework.\"InboxState\""));
        await host.StopAsync();
    }

    [Fact]
    public async Task UnacceptedOldAndRepeatedDispatchGenerationsHaveNoExtraEffects()
    {
        var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options);
        using var host=fixture.Host(); await host.StartAsync(); var old=ConsumptionFixture.Message(_broker.Options);
        await ConsumptionFixture.RegisterWorkAsync(_db,old); var current=old with {EventId=Guid.NewGuid(),DispatchSequence=2};
        await ConsumptionFixture.RegisterWorkAsync(_db,current,true); await ConsumptionFixture.SendAsync(host,old);
        await OutboxFixture.WaitAsync(async()=>await fixture.ConsumedAsync()==1);
        Assert.Equal(0,fixture.State.Calls); Assert.Equal(0,await fixture.EffectsAsync());
        await ConsumptionFixture.SendAsync(host,current); await OutboxFixture.WaitAsync(async()=>await fixture.EffectsAsync()==1);
        var repeated=current with {EventId=Guid.NewGuid()}; await ConsumptionFixture.RegisterWorkAsync(_db,repeated,true);
        await ConsumptionFixture.SendAsync(host,repeated); await OutboxFixture.WaitAsync(async()=>await fixture.ConsumedAsync()==3);
        Assert.Equal(1,await fixture.EffectsAsync()); Assert.Equal(1,await fixture.AuditsAsync()); Assert.Equal(3,await fixture.ResultsAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task ExplicitConsumerPermissionsAddOnlyInboxRightsAndDoNotAlterSchemaOrData()
    {
        var before=await _db.Runner.StatusAsync(default);
        var sender=new MigrationRunner(MigrationConfiguration.Create(_db.MigrationConnection,_db.WriterRole,_db.ReaderRole));
        await sender.ApplyAsync(default);
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(_db.WriterConnection,"SELECT has_table_privilege(current_user,'framework.\"InboxState\"','INSERT')"));
        var consumer=new MigrationRunner(MigrationConfiguration.Create(_db.MigrationConnection,_db.WriterRole,_db.ReaderRole,true));
        await consumer.ApplyAsync(default);
        Assert.True(await PersistenceDatabase.ScalarAsync<bool>(_db.WriterConnection,"SELECT has_table_privilege(current_user,'framework.\"InboxState\"','INSERT') AND has_sequence_privilege(current_user,'framework.\"InboxState_Id_seq\"','USAGE')"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(_db.ReaderConnection,"SELECT has_table_privilege(current_user,'framework.\"InboxState\"','INSERT')"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(_db.WriterConnection,"SELECT has_schema_privilege(current_user,'framework','CREATE') OR has_table_privilege(current_user,'aud.events','DELETE')"));
        Assert.Equal(before.Applied,(await consumer.StatusAsync(default)).Applied);
        var sql=consumer.GenerateScript(); Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE framework.\"InboxState\"",sql);
        Assert.DoesNotContain(new Npgsql.NpgsqlConnectionStringBuilder(_db.WriterConnection).Password!,sql);
        await using var provider=_db.CreateProvider(); await using var scope=provider.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<SvmDbContext>(); await using var transaction=await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>db.SaveChangesAsync());
        Assert.Equal(PersistenceFailure.InvalidTransactionNesting,(await Assert.ThrowsAsync<PersistenceException>(()=>scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(),_=>Task.FromResult(1),default))).Failure);
    }

    [Fact]
    public async Task TypedDispatchCannotBypassTheVerifiedNativeConsumptionTransaction()
    {
        var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options);
        using var host=fixture.Host(); await using var scope=host.Services.CreateAsyncScope();
        var message=ConsumptionFixture.Message(_broker.Options); await ConsumptionFixture.RegisterWorkAsync(_db,message);
        await scope.ServiceProvider.GetRequiredService<IIntegrationEventPreflight>().VerifyAsync(message,default);
        Assert.Equal(ConsumptionFailure.TransactionRequired,(await Assert.ThrowsAsync<IntegrationConsumptionException>(()=>
            scope.ServiceProvider.GetRequiredService<IIntegrationEventDispatcher<TaskPreparationAvailableV1>>().DispatchAsync(message,default))).Failure);
        Assert.Equal(0,fixture.State.Calls); Assert.Equal(0,await fixture.ResultsAsync());
    }
}
