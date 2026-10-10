using System.Diagnostics;
using System.Text.Json;
using MassTransit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class DeploymentBrowserTests
{
    [Fact]
    public async Task HttpsBrowserAndTwoWorkersRecoverPreparationControlAndPersistentDeduplication()
    {
        await using var f = await PackageHostFixture.CreateAsync(tasks:true); await f.GatewayAsync();
        var workers = new List<PackageProcess> { await f.WorkerAsync(f.A), await f.WorkerAsync(f.B) };
        var bodies = new Dictionary<Guid,string>();
        var run = Guid.NewGuid().ToString("N"); var password = Guid.NewGuid().ToString("N");
        var artifacts = Path.Combine(OutboxFixture.Root,"artifacts/deployment-update/browser-"+run);
        var coordination = Path.Combine(f.DirectoryPath,"coordination"); Directory.CreateDirectory(coordination);
        var config = await OutboxFixture.PrivateJsonAsync(new {url=f.GatewayUrl,employeeNo=PersonnelDatabase.EmployeeNo,initialPassword=f.Personnel.Password,password,artifacts,coordination});
        var info = new ProcessStartInfo(Path.Combine(OutboxFixture.Root,".tools/node/bin/node")) {WorkingDirectory=OutboxFixture.Root,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(Path.Combine(OutboxFixture.Root,"src/ui/svm-web/tests/browser/deployments.mjs"));
        info.Environment["SVM_DEPLOYMENT_BROWSER_CONFIG_FILE"]=config; info.Environment["PLAYWRIGHT_BROWSERS_PATH"]=Path.Combine(OutboxFixture.Root,".cache/playwright-browsers");
        using var process = new Process {StartInfo=info}; var started=false; Task<string>? stdout=null,stderr=null;
        try
        {
            Assert.True(started=process.Start()); stdout=process.StandardOutput.ReadToEndAsync(); stderr=process.StandardError.ReadToEndAsync();
            await Marker("offline-preparation"); await StopWorkers(); await Go("offline-preparation");
            var preparation=await Marker("created-preparation");
            await Capture(preparation);
            Assert.Equal(0,await Count("tsk.tasks")); Assert.True(await OutboxFixture.MessagesAsync(f.Personnel.Database)>0);
            await RestartWorkers(); await Go("created-preparation");
            await Marker("prepared"); Assert.Equal(1,await Count("tsk.tasks")); Assert.Equal(1,await Count("tsk.admission_items"));
            await StopWorkers(); await ClearInbox(); var preparationIds=await Replay(preparation,false); await RestartWorkers();
            await OutboxFixture.WaitAsync(async()=>await Delivered(preparationIds)==preparationIds.Count,30);
            Assert.Equal(1,await Count("tsk.tasks")); Assert.Equal(1,await Count("tsk.admission_items"));
            await Go("prepared");
            await Marker("offline-control"); await StopWorkers(); await Go("offline-control");
            var control=await Marker("created-control"); Assert.Equal(0,await Count("tsk.control_items"));
            await Capture(control);
            await RestartWorkers(); await Go("created-control");
            await Marker("control-finished"); Assert.Equal(1,await Count("tsk.control_items"));
            await StopWorkers(); await ClearInbox(); var controlIds=await Replay(control,true); await RestartWorkers();
            await OutboxFixture.WaitAsync(async()=>await Delivered(controlIds)==controlIds.Count,30); Assert.Equal(1,await Count("tsk.control_items")); Assert.Equal(2,await Count("tsk.tasks"));
            Assert.Equal(0,await f.Broker.QueueCountAsync("svm.task-preparation.available.v1_error"));
            Assert.Equal(0,await f.Broker.QueueCountAsync("svm.task-control.available.v1_error"));
            await Go("control-finished"); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var output=await stdout+await stderr; foreach(var secret in new[]{password,f.Personnel.Password}) { PersistenceDatabase.AssertRedacted(output,secret);f.ApiA.AssertRedacted(secret);f.ApiB.AssertRedacted(secret); }
            Directory.CreateDirectory(artifacts); await File.WriteAllTextAsync(Path.Combine(artifacts,"browser.log"),output);
            Assert.True(process.ExitCode==0,output);Assert.Contains("Browser deployment verification passed",output);
            Assert.Equal(1,await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection,"SELECT count(*) FROM tsk.tasks WHERE \"State\"='Succeeded'"));
            Assert.Equal(1,await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection,"SELECT count(*) FROM tsk.tasks WHERE \"State\"='Canceled'"));
        }
        catch
        {
            if(started && process.HasExited && stdout is not null && stderr is not null)
                Assert.Fail(await stdout+await stderr);
            throw;
        }
        finally { if(started && !process.HasExited) {process.Kill(true);await process.WaitForExitAsync();} File.Delete(config); }

        async Task<JsonElement> Marker(string name)
        { var path=Path.Combine(coordination,name+".json");await OutboxFixture.WaitAsync(()=>Task.FromResult(File.Exists(path)||process.HasExited),110);if(process.HasExited)Assert.Fail(await stdout!+await stderr!);using var json=JsonDocument.Parse(await File.ReadAllTextAsync(path));return json.RootElement.Clone(); }
        Task Go(string name)=>File.WriteAllTextAsync(Path.Combine(coordination,name+".go"),"continue");
        async Task StopWorkers(){foreach(var w in workers)await w.DisposeAsync();workers.Clear();}
        async Task RestartWorkers(){workers.Add(await f.WorkerAsync(f.A));workers.Add(await f.WorkerAsync(f.B));}
        Task<long> Count(string table)=>PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection,"SELECT count(*) FROM "+table);
        Task<long> Delivered(IReadOnlyList<Guid> ids)=>PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection,"SELECT count(*) FROM framework.\"InboxState\" WHERE \"Delivered\" IS NOT NULL AND \"MessageId\" IN ("+string.Join(',',ids.Select(id=>$"'{id:D}'"))+")");
        Task ClearInbox()=>PersistenceDatabase.ExecuteAsync(f.Personnel.Database.MigrationConnection,"DELETE FROM framework.\"InboxState\"");
        async Task Capture(JsonElement marker)
        {
            var work=marker.GetProperty("workId").GetGuid();
            var text=await PersistenceDatabase.ScalarAsync<string>(f.Personnel.Database.ReaderConnection,$"SELECT json_agg(json_build_object('eventId',d.\"Id\",'body',o.\"Body\") ORDER BY d.\"Sequence\")::text FROM tsk.dispatches d JOIN framework.\"OutboxMessage\" o ON o.\"MessageId\"=d.\"Id\" WHERE d.\"WorkId\"='{work:D}'");
            using var messages=JsonDocument.Parse(text);
            foreach(var message in messages.RootElement.EnumerateArray()) bodies.Add(message.GetProperty("eventId").GetGuid(),message.GetProperty("body").GetString()!);
        }
        async Task<IReadOnlyList<Guid>> Replay(JsonElement marker,bool control)
        {
            var work=marker.GetProperty("workId").GetGuid();
            var text=await PersistenceDatabase.ScalarAsync<string>(f.Personnel.Database.ReaderConnection,$"SELECT json_agg(json_build_object('eventId',\"Id\",'sequence',\"Sequence\") ORDER BY \"Sequence\")::text FROM tsk.dispatches WHERE \"WorkId\"='{work:D}'");
            using var dispatches=JsonDocument.Parse(text); Assert.True(dispatches.RootElement.GetArrayLength() >= (control?1:2));
            var ids=new List<Guid>();
            foreach(var dispatch in dispatches.RootElement.EnumerateArray())
            {
                var id=dispatch.GetProperty("eventId").GetGuid(); ids.Add(id);
                using var document=JsonDocument.Parse(bodies[id]); var message=document.RootElement;
                var raw=message.TryGetProperty("message",out _) ? bodies[id] : JsonSerializer.Serialize(new {messageId=id,messageType=new[]{control ? MessageUrn.ForType<TaskControlAvailableV1>().ToString() : MessageUrn.ForType<TaskPreparationAvailableV1>().ToString()},message});
                var queue=control ? "svm.task-control.available.v1" : "svm.task-preparation.available.v1";
                await f.Broker.PublishRawAsync(queue,raw); await f.Broker.PublishRawAsync(queue,raw);
            }
            return ids;
        }
    }
}
