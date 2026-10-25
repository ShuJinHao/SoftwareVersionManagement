using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Xunit;
using static Svm.FrameworkTests.InstanceHttpTests;
namespace Svm.FrameworkTests;
[Trait("Category","Business")]
public sealed class HardwareWorkspaceHttpTests
{
    [Fact]
    public async Task HttpsHardwareWorkspaceUsesGeneratedCodesBoundCursorsAndCurrentPermissions()
    {
        await using var f=await PersonnelDatabase.CreateAsync(); var site=new SiteCatalogOptions(Guid.NewGuid(),"硬件 HTTP 夹具","Asia/Shanghai");
        await using var api=await LocalApi.StartAsync(f.Database,site:site,instanceAccess:InstanceFixture.Limits);
        using var human=api.Client(new CookieContainer()); var session=await SiteCatalogHttpTests.AuthorizeAsync(f,api,human);
        var p=await Ok(await Write(human,api.Url,"processes",new { name="虚构工序" },session),201); Assert.StartsWith("PR-",p.GetProperty("code").GetString());
        var d=await Ok(await Write(human,api.Url,"devices",new { processId=Id(p),name="虚构设备",assetNumber="FIXTURE-ASSET" },session),201); Assert.StartsWith("EQ-",d.GetProperty("deviceNo").GetString());
        var h=await Ok(await Write(human,api.Url,$"devices/{Id(d)}/hardware",new { type="IPC",name="虚构工控机",managementIp="192.0.2.10" },session),201);
        var binding=await Ok(await Write(human,api.Url,$"hardware/{Id(h)}/software-bindings",new { newSoftware=new { name="虚构上位机",category="UpperComputer" },reason="创建并强关联" },session),201);
        var sid=binding.GetProperty("softwareId").GetGuid(); await SiteCatalogHttpTests.GrantSoftwareAsync(human,api,session,sid); var software=await Ok(await human.GetAsync(api.Url+"/api/v1/manage/software/"+sid)); Assert.StartsWith("SW-",software.GetProperty("code").GetString());
        var other=await Position(human,api.Url,session,Id(d),sid,"另一工控机");
        var url=$"/api/v1/manage/workspace/installations?softwareId={sid}&pageSize=1";
        var first=await Ok(await human.GetAsync(api.Url+url)); Assert.Equal(2,first.GetProperty("totalCount").GetInt64()); var row=Assert.Single(first.GetProperty("items").EnumerateArray()); Assert.Equal("NotRegistered",row.GetProperty("locationState").GetString()); Assert.Equal(JsonValueKind.Null,row.GetProperty("instance").ValueKind);
        var cursor=first.GetProperty("nextCursor").GetString()!; var second=await Ok(await human.GetAsync(api.Url+url+"&cursor="+Uri.EscapeDataString(cursor))); Assert.Single(second.GetProperty("items").EnumerateArray());
        await Code(await human.GetAsync(api.Url+url+"&name=changed&cursor="+Uri.EscapeDataString(cursor)),400,"INVALID_REQUEST");
        await Code(await Write(human,api.Url,"hardware/"+Id(h),new { deviceId=Guid.NewGuid(),name="改绑",expectedRevision=1,reason="拒绝移动" },session,method:HttpMethod.Patch),400,"UNKNOWN_FIELD");
        await Code(await human.PostAsJsonAsync(api.Url+$"/api/v1/manage/devices/{Id(d)}/hardware",new { type="IPC",name="无 CSRF" }),403,"PERMISSION_DENIED");
        var hardware=await Ok(await human.GetAsync(api.Url+$"/api/v1/manage/devices/{Id(d)}/hardware")); Assert.Equal(2,hardware.GetProperty("items").GetArrayLength());
        var overview=await Ok(await human.GetAsync(api.Url+"/api/v1/manage/workspace/devices")); Assert.Equal(2,Assert.Single(overview.GetProperty("items").EnumerateArray()).GetProperty("installationCount").GetInt32());
        var user=await Ok(await human.GetAsync(api.Url+"/api/v1/manage/users/"+session.GetProperty("subjectId").GetGuid()));
        await SiteCatalogHttpTests.Grant(human,api,session,user,Permissions(user).Where(x=>x.Operation!="instance.read").ToArray());
        await Code(await human.GetAsync(api.Url+url+"&cursor="+Uri.EscapeDataString(cursor)),400,"INVALID_REQUEST");
        Assert.Equal(0,(await Ok(await human.GetAsync(api.Url+url))).GetProperty("totalCount").GetInt64());
    }
}
