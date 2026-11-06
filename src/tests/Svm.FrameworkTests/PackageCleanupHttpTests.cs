using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Packages;
using Xunit;
using static Svm.FrameworkTests.PackageHttpTests;
namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class PackageCleanupHttpTests
{
    [Fact] public async Task RealCleanupSurvivesAckExitAndOneUnavailableNodeThenTwoWorkersFinishWithoutLosingHistory()
    {
        await using var f=await PackageHostFixture.CreateAsync();using var client=f.Client(new CookieContainer());var setup=await Setup(f,client);
        var session=await Get<JsonElement>(client,f.ApiA.Url+"/api/v1/session");var userId=session.GetProperty("subjectId").GetGuid();
        var user=await Get<UserView>(client,f.ApiA.Url+"/api/v1/manage/users/"+userId);
        await Write<UserView>(client,f.ApiA.Url,setup.Csrf,$"subjects/{userId}/permissions",new{permissions=user.Permissions.Append(new PermissionView(setup.Software.Id,"package.clean")),expectedRevision=user.Revision,reason="清理夹具显式授权"},HttpMethod.Put);
        var bytes=RandomNumberGenerator.GetBytes(1024*1024+41);var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var r=await Write<ReleaseUploadResult>(client,f.ApiA.Url,setup.Csrf,$"software/{setup.Software.Id}/releases",new{changeLevel="Patch",changeSummary="清理历史保留夹具",changeReason="虚构包清理验证",package=new{fileName="cleanup.zip",sizeBytes=bytes.Length,sha256=hash}});
        using(var uploaded=await PutBytes(client,f.ApiA.Url+r.UploadPath,bytes,setup.Csrf))Assert.Equal(HttpStatusCode.Accepted,uploaded.StatusCode);
        var first=await f.WorkerAsync(f.A);
        await OutboxFixture.WaitAsync(async()=>(await Get<ReleaseView>(client,f.ApiA.Url+"/api/v1/manage/releases/"+r.Release.Id)).State=="Test",40);
        await f.GatewayAsync();var path=f.GatewayUrl+"/api/v1/packages/"+r.Release.PackageId+"/content";
        using(var head=await client.SendAsync(new(HttpMethod.Head,path)))Assert.Equal(HttpStatusCode.OK,head.StatusCode);
        using(var range=new HttpRequestMessage(HttpMethod.Get,path))
        {range.Headers.Range=new(1,99);using var response=await client.SendAsync(range);Assert.Equal(HttpStatusCode.PartialContent,response.StatusCode);Assert.Equal(bytes.Skip(1).Take(99),await response.Content.ReadAsByteArrayAsync());}
        await OutboxFixture.WaitAsync(async()=>await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection,"SELECT count(*) FROM pkg.download_sessions WHERE \"EndedAt\" IS NOT NULL")==1,30);
        var release=await Get<ReleaseView>(client,f.ApiA.Url+"/api/v1/manage/releases/"+r.Release.Id);
        await Write<ReleaseView>(client,f.ApiA.Url,setup.Csrf,$"releases/{release.Id}/disable",new{reason="明确停用后清理",expectedRevision=release.Revision});
        await first.DisposeAsync();
        var status=await Get<PackageCleanupStatus>(client,f.ApiA.Url+$"/api/v1/manage/packages/{r.Release.PackageId}/cleanup");Assert.Empty(status.BlockingReasons);
        using(var bad=new HttpRequestMessage(HttpMethod.Post,f.ApiA.Url+$"/api/v1/manage/packages/{r.Release.PackageId}/cleanup"){Content=JsonContent.Create(new{expectedRevision=status.Revision,reason="非法字段",filePath="/tmp/not-allowed"})})
        {bad.Headers.Add("X-CSRF-TOKEN",setup.Csrf);bad.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString());using var response=await client.SendAsync(bad);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);}
        using(var noCsrf=new HttpRequestMessage(HttpMethod.Post,f.ApiA.Url+$"/api/v1/manage/packages/{r.Release.PackageId}/cleanup"){Content=JsonContent.Create(new{expectedRevision=status.Revision,reason="缺少防伪"})})
        {noCsrf.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString());using var response=await client.SendAsync(noCsrf);Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);}
        var key=Guid.NewGuid();var body=new{expectedRevision=status.Revision,reason="真实清理恢复"};
        var work=await Write<PackageWorkView>(client,f.ApiA.Url,setup.Csrf,$"packages/{r.Release.PackageId}/cleanup",body,key:key);
        var originalMessage=await PersistenceDatabase.ScalarAsync<string>(f.Personnel.Database.ReaderConnection,"SELECT \"Body\" FROM framework.\"OutboxMessage\" ORDER BY \"SequenceNumber\" DESC LIMIT 1");
        // Hold a real file mutex, let the consumer commit its fact, then kill the executor.
        using(var held=new FileStream(Path.Combine(f.A.RootPath,"locks","package-"+r.Release.PackageId.ToString("D")+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
        {
            var interrupted=await f.WorkerAsync(f.A);
            await OutboxFixture.WaitAsync(async()=>(await Get<PackageWorkView>(client,f.ApiA.Url+"/api/v1/manage/package-work/"+work.Id)).State=="Running",35);
            await interrupted.DisposeAsync();Assert.True(File.Exists(f.Replica(f.A,r.Release.PackageId)));
        }
        await f.ApiB.DisposeAsync();await f.WorkerAsync(f.A);
        await OutboxFixture.WaitAsync(async()=>(await Get<PackageWorkView>(client,f.ApiA.Url+"/api/v1/manage/package-work/"+work.Id)).State=="Failed",45);
        Assert.False(File.Exists(f.Replica(f.A,r.Release.PackageId)));Assert.True(File.Exists(f.Replica(f.B,r.Release.PackageId)));
        Assert.Equal(1,(await Get<PackageWorkView>(client,f.ApiA.Url+"/api/v1/manage/package-work/"+work.Id)).CompletedItems);
        await f.RestartApiBAsync();status=await Get<PackageCleanupStatus>(client,f.ApiA.Url+$"/api/v1/manage/packages/{r.Release.PackageId}/cleanup");
        Assert.Equal(work.Id,(await Write<PackageWorkView>(client,f.ApiA.Url,setup.Csrf,$"packages/{r.Release.PackageId}/cleanup",new{expectedRevision=status.Revision,reason="恢复剩余节点"})).Id);
        await f.WorkerAsync(f.B);
        await OutboxFixture.WaitAsync(async()=>(await Get<PackageCleanupStatus>(client,f.ApiA.Url+$"/api/v1/manage/packages/{r.Release.PackageId}/cleanup")).State=="Cleaned",40);
        Assert.False(File.Exists(f.Replica(f.B,r.Release.PackageId)));Assert.False(File.Exists(Path.Combine(f.A.RootPath,"staging",r.UploadId.ToString("D")+".verified")));
        Assert.Equal("Completed",(await Write<PackageWorkView>(client,f.ApiA.Url,setup.Csrf,$"packages/{r.Release.PackageId}/cleanup",body,key:key)).State);
        await PersistenceDatabase.ExecuteAsync(f.Personnel.Database.MigrationConnection,"DELETE FROM framework.\"InboxState\"");
        using(var doc=JsonDocument.Parse(originalMessage))
        {
            var raw=doc.RootElement.TryGetProperty("message",out _)?originalMessage:JsonSerializer.Serialize(new{messageId=doc.RootElement.GetProperty("eventId").GetGuid(),messageType=new[]{"urn:message:Svm.Services.Contracts.Messaging.V1:PackageWorkAvailableV1"},message=doc.RootElement});
            await f.Broker.PublishRawAsync("svm.package-work.available.v1",raw);
        }
        await OutboxFixture.WaitAsync(async()=>await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection,"SELECT count(*) FROM framework.\"InboxState\" WHERE \"Consumed\" IS NOT NULL")>0,30);
        Assert.Equal(1,await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection,"SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.cleanup.completed'"));
        Assert.Equal(1,await f.Personnel.CountAsync("pkg.cleanup_intents"));Assert.Equal(1,await f.Personnel.CountAsync("pkg.download_sessions"));
        Assert.Equal("Disabled",(await Get<ReleaseView>(client,f.ApiA.Url+"/api/v1/manage/releases/"+r.Release.Id)).State);
        using var denied=await client.GetAsync(path);Assert.False(denied.IsSuccessStatusCode);
        f.ApiA.AssertRedacted(setup.Password);f.ApiB.AssertRedacted(setup.Password);
    }
}
