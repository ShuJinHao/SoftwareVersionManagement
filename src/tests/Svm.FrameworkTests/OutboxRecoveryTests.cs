using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class OutboxRecoveryTests : IAsyncLifetime
{
    private readonly PersistenceDatabase _database = new();
    private readonly OutboxBroker _broker = new();
    public async Task InitializeAsync() { await _database.InitializeAsync(); await _broker.InitializeAsync(); }
    public async Task DisposeAsync() { try { await _broker.DisposeAsync(); } finally { await _database.DisposeAsync(); } }

    [Fact]
    public async Task AllThreeQueuesAreDurableQuorumAndKeepMessagesWithoutBusinessConsumers()
    {
        var fixture = new IdempotencyFixture(_database); var options = _broker.Options;
        var pkg = OutboxFixture.Message(options);
        var prep = new TaskPreparationAvailableV1(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), null, options.SiteId, Guid.NewGuid(), TaskPreparationWorkKind.TargetSelection, Guid.NewGuid(), 1);
        var control = new TaskControlAvailableV1(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), null, options.SiteId, Guid.NewGuid(), TaskControlWorkKind.Cancel, Guid.NewGuid(), 1);
        await using (var producer = OutboxFixture.Provider(fixture, options))
        {
            await OutboxFixture.StageAsync(fixture, producer, [pkg]);
            foreach (var message in new IIntegrationEvent[] { prep, control })
                await fixture.ExecuteAsync(producer, Guid.NewGuid(), owner: ModuleOwner.Tasks, action: async (scope, token) =>
                {
                    await scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>().EnqueueAsync(message, token);
                    return await fixture.ApplyAsync(scope, token);
                });
        }
        await using (var worker = await OutboxWorker.StartAsync(_database, options))
            await OutboxFixture.WaitAsync(async () => await OutboxFixture.MessagesAsync(_database) == 0);
        foreach (var message in new IIntegrationEvent[] { pkg, prep, control })
        {
            using var queue = await _broker.GetAsync("queues/" + _broker.Vhost + "/" + message.MessageType);
            Assert.Equal("quorum", queue.RootElement.GetProperty("type").GetString());
            Assert.True(queue.RootElement.GetProperty("durable").GetBoolean()); Assert.False(queue.RootElement.GetProperty("auto_delete").GetBoolean());
            using var consumers = await _broker.GetAsync("consumers/" + _broker.Vhost);
            Assert.DoesNotContain(consumers.RootElement.EnumerateArray(), c => c.GetProperty("queue").GetProperty("name").GetString() == message.MessageType);
            var received = Assert.Single(await _broker.TakeAsync(message.MessageType));
            Assert.Equal(2, received.GetProperty("properties").GetProperty("delivery_mode").GetInt32());
            using var body = JsonDocument(received); Assert.Equal(message.EventId, body.RootElement.GetProperty("messageId").GetGuid());
            Assert.Equal(message.MessageType, body.RootElement.GetProperty("message").GetProperty("messageType").GetString());
        }
        Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(_database.ReaderConnection, "SELECT count(*) FROM framework.\"InboxState\""));
    }

    [Fact]
    public async Task StartupWithoutBrokerPersistsNewWorkAndWorkerRestartDrainsItAfterBrokerRecovery()
    {
        var fixture = new IdempotencyFixture(_database); var options = _broker.Options; var message = OutboxFixture.Message(options);
        await _broker.StopAsync();
        try
        {
            await using (var worker = await OutboxWorker.StartAsync(_database, options))
            {
                await using var producer = OutboxFixture.Provider(fixture, options);
                await OutboxFixture.StageAsync(fixture, producer, [message]).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, await OutboxFixture.MessagesAsync(_database)); Assert.Equal(1, fixture.Calls);
            }
            Assert.Equal(1, await OutboxFixture.MessagesAsync(_database));
        }
        finally { await _broker.StartAsync(); }
        await using (var restarted = await OutboxWorker.StartAsync(_database, options))
            await OutboxFixture.WaitAsync(async () => await OutboxFixture.MessagesAsync(_database) == 0);
        var received = Assert.Single(await _broker.TakeAsync()); using var body = JsonDocument(received);
        Assert.Equal(message.EventId, body.RootElement.GetProperty("messageId").GetGuid()); Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task RuntimeDisconnectionAndTwoConcurrentWorkersHaveNoLostMessages()
    {
        var fixture = new IdempotencyFixture(_database); var options = _broker.Options;
        var messages = Enumerable.Range(0, 24).Select(_ => OutboxFixture.Message(options)).ToArray();
        await using var first = await OutboxWorker.StartAsync(_database, options);
        await using var second = await OutboxWorker.StartAsync(_database, options);
        // Make both native delivery services healthy before disconnecting the broker.
        await using var producer = OutboxFixture.Provider(fixture, options);
        await OutboxFixture.StageAsync(fixture, producer, [messages[0]]);
        await OutboxFixture.WaitAsync(async () => await OutboxFixture.MessagesAsync(_database) == 0);
        await _broker.StopAsync();
        try
        {
            await Task.WhenAll(messages.Skip(1).Select(m => OutboxFixture.StageAsync(fixture, producer, [m])));
            Assert.Equal(23, await OutboxFixture.MessagesAsync(_database));
        }
        finally { await _broker.StartAsync(); }
        await OutboxFixture.WaitAsync(async () => await OutboxFixture.MessagesAsync(_database) == 0, 50);
        var received = await _broker.TakeAsync(); var ids = received.Select(e => { using var body = JsonDocument(e); return body.RootElement.GetProperty("messageId").GetGuid(); }).ToArray();
        Assert.Equal(messages.Select(m => m.EventId).Order(), ids.Distinct().Order());
        Assert.Equal(24, fixture.Calls);
    }

    [Fact]
    public async Task LostPublisherConfirmKeepsPendingIntentAndRetransmitsTheSameMessageIdAndBody()
    {
        var fixture = new IdempotencyFixture(_database); var options = _broker.Options; var message = OutboxFixture.Message(options);
        await using var proxy = new OutboxConfirmProxy(options.Port); proxy.Start();
        await using var producer = OutboxFixture.Provider(fixture, options);
        await OutboxFixture.StageAsync(fixture, producer, [message]);
        await using var worker = await OutboxWorker.StartAsync(_database, options with { Port = proxy.Port });
        try
        {
            await proxy.Dropped.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, await OutboxFixture.MessagesAsync(_database));
            await OutboxFixture.WaitAsync(async () => await _broker.QueueCountAsync() >= 1);
        }
        finally { proxy.Resume(); }
        await OutboxFixture.WaitAsync(async () => await OutboxFixture.MessagesAsync(_database) == 0, 50);
        var received = await _broker.TakeAsync(); Assert.True(received.Count >= 2, "The broker must retain the accepted original and its retransmission.");
        var payloads = received.Select(e => e.GetProperty("payload").GetString()).ToArray(); Assert.Single(payloads.Distinct());
        foreach (var copy in received) { using var body = JsonDocument(copy); Assert.Equal(message.EventId, body.RootElement.GetProperty("messageId").GetGuid()); }
        Assert.Equal(1, fixture.Calls);
    }
    private static System.Text.Json.JsonDocument JsonDocument(System.Text.Json.JsonElement message) => System.Text.Json.JsonDocument.Parse(message.GetProperty("payload").GetString()!);
}
