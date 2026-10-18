using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Svm.Services.Contracts.Packages;
using Xunit;
using static Svm.FrameworkTests.PackageHttpTests;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageDownloadRecoveryTests
{
    [Fact] public async Task HealthyLocalBypassesHangingPeerAndRemoteFallbackStillRequiresVerifiedBytes()
    {
        await using var f = await PackageHostFixture.CreateAsync(copyProxy:true); using var client=f.Client(new CookieContainer()); var setup=await Setup(f,client);
        var bytes=RandomNumberGenerator.GetBytes(1048591); var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var r=await Write<ReleaseUploadResult>(client,f.ApiA.Url,setup.Csrf,$"software/{setup.Software.Id}/releases",new {changeLevel="Patch",changeSummary="单副本下载夹具",changeReason="慢节点隔离",package=new {fileName="single-replica.zip",sizeBytes=bytes.Length,sha256=hash}});
        using(var uploaded=await PutBytes(client,f.ApiA.Url+r.UploadPath,bytes,setup.Csrf)) Assert.Equal(HttpStatusCode.Accepted,uploaded.StatusCode);
        var worker=await f.WorkerAsync(f.A); await OutboxFixture.WaitAsync(async()=> (await Get<ReleaseView>(client,f.ApiA.Url+"/api/v1/manage/releases/"+r.Release.Id)).State=="Test",35); await worker.DisposeAsync();
        await f.GatewayAsync(); var path=f.GatewayUrl+"/api/v1/packages/"+r.Release.PackageId+"/content";
        var remoteBefore=await PersistenceDatabase.ScalarAsync<DateTime>(f.Personnel.Database.ReaderConnection,"SELECT \"CheckedAt\" FROM pkg.replicas WHERE \"NodeId\"='node-b'");
        f.CopyProxy!.HoldIntegrity(); var watch=Stopwatch.StartNew();
        using(var head=await client.SendAsync(new(HttpMethod.Head,path))) Assert.Equal(HttpStatusCode.OK,head.StatusCode);
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(5)); Assert.Equal(0,await f.Personnel.CountAsync("pkg.download_sessions"));
        using(var request=new HttpRequestMessage(HttpMethod.Get,path)) {request.Headers.Range=new(3,1026);using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.PartialContent,response.StatusCode);Assert.Equal(bytes.Skip(3).Take(1024).ToArray(),await response.Content.ReadAsByteArrayAsync());}
        Assert.Equal(remoteBefore,await PersistenceDatabase.ScalarAsync<DateTime>(f.Personnel.Database.ReaderConnection,"SELECT \"CheckedAt\" FROM pkg.replicas WHERE \"NodeId\"='node-b'"));
        Assert.Equal(1,await f.Personnel.CountAsync("pkg.download_sessions"));
        f.CopyProxy.ResumeIntegrity(); await File.WriteAllBytesAsync(f.Replica(f.A,r.Release.PackageId),[0,1,2]);
        using(var fallback=await client.GetAsync(path)) {Assert.Equal(HttpStatusCode.OK,fallback.StatusCode);Assert.Equal(bytes,await fallback.Content.ReadAsByteArrayAsync());}
        File.Delete(f.Replica(f.B,r.Release.PackageId)); using(var denied=await client.GetAsync(path)) Assert.False(denied.IsSuccessStatusCode);
        Assert.Equal(2,await f.Personnel.CountAsync("pkg.download_sessions"));
        var release=await Get<ReleaseView>(client,f.ApiA.Url+"/api/v1/manage/releases/"+r.Release.Id);
        await Write<ReleaseView>(client,f.ApiA.Url,setup.Csrf,$"releases/{release.Id}/disable",new {expectedRevision=release.Revision,reason="虚构停用验证"});
        using(var disabled=await client.GetAsync(path)) Assert.Equal(HttpStatusCode.Forbidden,disabled.StatusCode);
    }
}
