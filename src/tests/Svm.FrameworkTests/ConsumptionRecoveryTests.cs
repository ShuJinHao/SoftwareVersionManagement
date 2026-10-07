using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Svm.EventBus;
using Svm.Services.Contracts.Messaging.V1;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class ConsumptionRecoveryTests : IAsyncLifetime
{
    private readonly PersistenceDatabase _db=new();
    private readonly OutboxBroker _broker=new();
    public async Task InitializeAsync() { await _db.InitializeAsync(); await _broker.InitializeAsync(); await ConsumptionFixture.PrepareAsync(_db); }
    public async Task DisposeAsync() { try {await _broker.DisposeAsync();} finally {await _db.DisposeAsync();} }

    [Theory]
    [InlineData("beforeCommit")]
    [InlineData("afterCommit")]
    public async Task ATestOnlyProcessCanExitAtEitherSideOfCommitAndRecover(string phase)
    {
        var state=new ConsumptionTestState(); var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options,state);
        var message=ConsumptionFixture.Message(_broker.Options); await ConsumptionFixture.RegisterWorkAsync(_db,message);
        await using (var child=await ConsumptionChild.StartAsync(_db,_broker.Options,state.ServiceId,phase))
        {
            await PublishAsync(message);
            await child.WaitMarkerAsync(phase=="beforeCommit"?"entered":"committed");
            Assert.Equal(phase=="beforeCommit"?0:1,await fixture.ConsumedAsync());
            Assert.Equal(phase=="beforeCommit"?0:1,await fixture.EffectsAsync());
        }
        await using (var restart=await ConsumptionChild.StartAsync(_db,_broker.Options,state.ServiceId,"normal"))
            await OutboxFixture.WaitAsync(async()=>await fixture.ConsumedAsync()==1 && await DeliveredAsync()==1);
        Assert.Equal(1,await fixture.EffectsAsync()); Assert.Equal(1,await fixture.ResultsAsync()); Assert.Equal(1,await fixture.AuditsAsync());
    }

    [Fact]
    public async Task OutgoingConfirmationLossAndProcessRestartKeepOriginalMessages()
    {
        var state=new ConsumptionTestState(); var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options,state);
        var message=ConsumptionFixture.Message(_broker.Options); await ConsumptionFixture.RegisterWorkAsync(_db,message);
        await using var proxy=new OutboxConfirmProxy(_broker.Options.Port); proxy.Start();
        await using (var child=await ConsumptionChild.StartAsync(_db,_broker.Options with {Port=proxy.Port},state.ServiceId,"outgoing"))
        {
            await PublishAsync(message); await proxy.Dropped.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1,await fixture.ConsumedAsync()); Assert.Equal(1,await fixture.EffectsAsync());
            Assert.Equal(2,await OutboxFixture.MessagesAsync(_db));
        }
        proxy.Resume();
        await using (var restart=await ConsumptionChild.StartAsync(_db,_broker.Options,state.ServiceId,"normal"))
            await OutboxFixture.WaitAsync(async()=>await OutboxFixture.MessagesAsync(_db)==0 && await DeliveredAsync()==1);
        var copies=await _broker.TakeAsync("svm.task-control.available.v1"); Assert.True(copies.Count>=3);
        var bodies=copies.Select(e=>e.GetProperty("payload").GetString()!).ToArray();
        var ids=bodies.Select(b=>{using var json=JsonDocument.Parse(b);return json.RootElement.GetProperty("messageId").GetGuid();}).ToArray();
        Assert.Equal(2,ids.Distinct().Count()); Assert.Equal(2,bodies.Distinct().Count()); Assert.Equal(1,await fixture.EffectsAsync());
    }

    [Fact]
    public async Task TwoConsumersAndConcurrentDuplicatesProduceOneAcceptance()
    {
        var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options); using var first=fixture.Host(); using var second=fixture.Host();
        await first.StartAsync(); await second.StartAsync();
        var message=ConsumptionFixture.Message(_broker.Options); await ConsumptionFixture.RegisterWorkAsync(_db,message);
        await Task.WhenAll(Enumerable.Range(0,12).Select(_=>ConsumptionFixture.SendAsync(first,message)));
        await OutboxFixture.WaitAsync(async()=>await fixture.ConsumedAsync()==1 && fixture.State.Preflights>=12);
        Assert.Equal(1,await fixture.EffectsAsync()); Assert.Equal(1,await fixture.ResultsAsync()); Assert.Equal(1,await fixture.AuditsAsync()); Assert.Equal(1,fixture.State.Calls);
        Assert.Equal(0,await _broker.QueueCountAsync("svm.task-preparation.available.v1_error"));
        await first.StopAsync(); await second.StopAsync();
    }

    [Theory]
    [InlineData(2,true)]
    [InlineData(4,false)]
    public async Task OnlyKnownUncommittedFailuresGetThreeRetriesWithFreshScopes(int failures,bool succeeds)
    {
        var fixture=new ConsumptionFixture(_db.WriterConnection,_db.ReaderConnection,_broker.Options); fixture.State.TransientFailures=failures;
        using var host=fixture.Host(); await host.StartAsync(); var message=ConsumptionFixture.Message(_broker.Options);
        await ConsumptionFixture.RegisterWorkAsync(_db,message); var timer=Stopwatch.StartNew(); await ConsumptionFixture.SendAsync(host,message);
        await OutboxFixture.WaitAsync(async()=>succeeds?await fixture.ConsumedAsync()==1:await _broker.QueueCountAsync("svm.task-preparation.available.v1_error")>=1);
        Assert.Equal(succeeds?3:4,fixture.State.Calls); Assert.Equal(fixture.State.Calls,fixture.State.HandlerScopes.Distinct().Count());
        Assert.Equal(succeeds?1:0,await fixture.EffectsAsync()); Assert.Equal(succeeds?1:0,await fixture.ResultsAsync());
        Assert.True(timer.Elapsed>=TimeSpan.FromSeconds(succeeds?4:9));
        await host.StopAsync();
    }

    private Task<long> DeliveredAsync()=>PersistenceDatabase.ScalarAsync<long>(_db.ReaderConnection,"SELECT count(*) FROM framework.\"InboxState\" WHERE \"Delivered\" IS NOT NULL");
    private Task PublishAsync(TaskPreparationAvailableV1 message)=>_broker.PublishRawAsync(message.MessageType,JsonSerializer.Serialize(new
    {
        messageId=message.EventId,correlationId=message.CorrelationId,
        messageType=new[]{MessageUrn.ForType<TaskPreparationAvailableV1>().ToString()},message
    },new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
}

internal sealed record ConsumptionHarnessConfiguration(string Writer,string Reader,MessagingOptions Messaging,Guid ServiceId,string Phase,string Marker);

// Invoked only by the private child-process verifier, excluded from Category=Business.
[Trait("Category","ConsumptionHarness")]
public sealed class ConsumptionProcessHarness
{
    [Fact]
    public async Task RunOwnedConsumer()
    {
        var path=Environment.GetEnvironmentVariable("SVM_CONSUMPTION_HARNESS_CONFIG") ?? throw new InvalidOperationException("Use the owned consumption child-process verifier.");
        Assert.Equal(Path.Combine(OutboxFixture.Root,".cache/outbox-tests"),Path.GetDirectoryName(Path.GetFullPath(path)));
        var config=JsonSerializer.Deserialize<ConsumptionHarnessConfiguration>(await File.ReadAllTextAsync(path),new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase})!;
        var writer=new Npgsql.NpgsqlConnectionStringBuilder(config.Writer);
        Assert.StartsWith("svm_test_",writer.Database); Assert.Equal("127.0.0.1",writer.Host);
        Assert.Equal(Path.GetFileNameWithoutExtension(path),Path.GetFileName(config.Marker));
        Assert.Equal(Path.GetDirectoryName(path),Path.GetDirectoryName(config.Marker));
        Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(path),"N",out _));
        Assert.Equal("svmt_"+writer.Database!["svm_test_".Length..]+"_w",writer.Username);
        var state=new ConsumptionTestState {ServiceId=config.ServiceId,Marker=config.Marker,Outgoing=config.Phase=="outgoing",Failure=config.Phase};
        if(config.Phase=="beforeCommit") state.BeforeReturn=token=>Task.Delay(Timeout.Infinite,token);
        var fixture=new ConsumptionFixture(config.Writer,config.Reader,config.Messaging,state);
        using var host=fixture.Host(new ConsumptionCommitHook(state)); await host.StartAsync();
        await host.Services.GetRequiredService<IBusControl>().StartAsync();
        await File.WriteAllTextAsync(config.Marker+".ready","ready");
        await Task.Delay(Timeout.Infinite);
    }
}

internal sealed class ConsumptionChild : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _config;
    private readonly string _marker;
    private readonly Task<string> _stdout,_stderr;
    private readonly string[] _secrets;
    private ConsumptionChild(Process process,string config,string marker,string[] secrets)
    { _process=process;_config=config;_marker=marker;_secrets=secrets;_stdout=process.StandardOutput.ReadToEndAsync();_stderr=process.StandardError.ReadToEndAsync(); }
    internal static async Task<ConsumptionChild> StartAsync(PersistenceDatabase db,MessagingOptions options,Guid serviceId,string phase)
    {
        var path=await OutboxFixture.PrivateJsonAsync(new { }); var marker=Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path));
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new ConsumptionHarnessConfiguration(db.WriterConnection,db.ReaderConnection,options,serviceId,phase,marker),new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        var info=new ProcessStartInfo(Path.Combine(OutboxFixture.Root,"eng/dotnet")) {WorkingDirectory=OutboxFixture.Root,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var arg in new[]{"test","src/tests/Svm.FrameworkTests/Svm.FrameworkTests.csproj","--no-build","--filter","FullyQualifiedName=Svm.FrameworkTests.ConsumptionProcessHarness.RunOwnedConsumer","--verbosity","quiet"}) info.ArgumentList.Add(arg);
        info.Environment["SVM_CONSUMPTION_HARNESS_CONFIG"]=path;
        info.Environment["Logging__LogLevel__Default"]="Warning";
        var process=Process.Start(info)!; var child=new ConsumptionChild(process,path,marker,[options.Password,new Npgsql.NpgsqlConnectionStringBuilder(db.WriterConnection).Password!]);
        try {await child.WaitMarkerAsync("ready");return child;} catch {await child.DisposeAsync();throw;}
    }
    internal Task WaitMarkerAsync(string phase)=>OutboxFixture.WaitAsync(()=>Task.FromResult(!_process.HasExited && File.Exists(_marker+"."+phase)),25);
    public async ValueTask DisposeAsync()
    {
        string output;
        try
        {
            if(!_process.HasExited)_process.Kill(entireProcessTree:true); await _process.WaitForExitAsync();
            output=await _stdout+await _stderr;
        }
        finally
        {
            var directory=Path.GetDirectoryName(_config)!;foreach(var path in Directory.GetFiles(directory,Path.GetFileName(_marker)+"*"))File.Delete(path);
            _process.Dispose();
        }
        foreach(var secret in _secrets)PersistenceDatabase.AssertRedacted(output,secret);
        await File.WriteAllTextAsync(Path.Combine(OutboxFixture.Root,"artifacts","consumer-child-"+Guid.NewGuid().ToString("N")+".log"),output);
    }
}
