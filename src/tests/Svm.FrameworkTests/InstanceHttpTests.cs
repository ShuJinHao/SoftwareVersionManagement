using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class InstanceHttpTests
{
    [Fact]
    public async Task TwoHttpsNodesRegisterReportRecoverAndRevokeWithSafeDocumentedResponses()
    {
        await using var db=await PersonnelDatabase.CreateAsync(); var site=new SiteCatalogOptions(Guid.NewGuid(),"接入 HTTP 夹具","Asia/Shanghai");
        await using var a=await LocalApi.StartAsync(db.Database,site:site,instanceAccess:InstanceFixture.Limits); await using var b=await LocalApi.StartAsync(db.Database,site:site,instanceAccess:InstanceFixture.Limits);
        using var human=a.Client(new CookieContainer()); using var machine=a.Client(new CookieContainer()); var session=await SiteCatalogHttpTests.AuthorizeAsync(db,a,human);
        var process=await Ok(await Write(human,a.Url,"processes",new { code="ENROLL-P",name="接入工序" },session),201);
        var device=await Ok(await Write(human,a.Url,"devices",new { processId=Id(process),deviceNo="ENROLL-D",name="接入设备" },session),201);
        var software=await Ok(await Write(human,a.Url,"software",new { code="ENROLL-S",name="视觉接入夹具",category="Vision" },session),201); var sid=Id(software); var did=Id(device);
        var user=await Ok(await human.GetAsync(a.Url+"/api/v1/manage/users/"+session.GetProperty("subjectId").GetGuid()));
        await SiteCatalogHttpTests.Grant(human,a,session,user,Permissions(user).Append(new(sid,"enrollment.manage")).ToArray());
        await Ok(await Write(human,a.Url,$"devices/{did}/software-bindings",new { softwareId=sid,reason="夹具映射" },session),201);
        var grantSecret=InstanceFixture.Secret(); var grant=await Ok(await Write(human,a.Url,"enrollment-grants",new { softwareId=sid,deviceIds=new[] {did},expiresAt=DateTimeOffset.UtcNow.AddHours(1),maxInstances=2,secretMaterial=grantSecret,reason="HTTP 登记许可" },session),201);
        await Code(await Write(human,a.Url,"enrollment-grants",new { softwareId=sid,deviceIds=new[] {did},expiresAt=DateTimeOffset.UtcNow.AddHours(49),maxInstances=2,secretMaterial=InstanceFixture.Secret(),reason="超出有效期上限" },session),422,"VALIDATION_FAILED");
        var bearer=Id(grant)+"."+grantSecret; var instanceSecret=InstanceFixture.Secret(); var key=Guid.NewGuid(); var body=new { softwareId=sid,installationKey=Guid.NewGuid(),deviceId=did,secretMaterial=instanceSecret };
        await Code(await Machine(machine,a.Url,"/api/v1/enrollment/instances",bearer,body),400,"INVALID_REQUEST");
        await Code(await Machine(machine,a.Url,"/api/v1/enrollment/instances",bearer,new { body.softwareId,body.installationKey,deviceId=Guid.NewGuid(),body.secretMaterial },Guid.NewGuid()),404,"RESOURCE_NOT_FOUND");
        var outside=await Ok(await Write(human,a.Url,"devices",new { processId=Id(process),deviceNo="OUTSIDE-GRANT",name="授权范围外设备" },session),201);
        await Code(await Machine(machine,a.Url,"/api/v1/enrollment/instances",bearer,new { body.softwareId,body.installationKey,deviceId=Id(outside),body.secretMaterial },Guid.NewGuid()),403,"PERMISSION_DENIED");
        var registration=await Ok(await Machine(machine,a.Url,"/api/v1/enrollment/instances",bearer,body,key),201); var id=registration.GetProperty("instanceId").GetGuid(); var credential=registration.GetProperty("credentialId").GetGuid(); var instanceBearer=credential+"."+instanceSecret;
        await Ok(await Machine(machine,a.Url,"/api/v1/enrollment/instances",bearer,new { body.softwareId,installationKey=Guid.NewGuid(),body.deviceId,secretMaterial=InstanceFixture.Secret() },Guid.NewGuid()),201);
        var listUrl=$"/api/v1/manage/instances?softwareId={sid}&pageSize=1"; var firstPage=await Ok(await human.GetAsync(a.Url+listUrl)); var cursor=firstPage.GetProperty("nextCursor").GetString()!; Assert.Single(firstPage.GetProperty("items").EnumerateArray());
        var secondPage=await Ok(await human.GetAsync(b.Url+listUrl+"&cursor="+Uri.EscapeDataString(cursor))); Assert.Single(secondPage.GetProperty("items").EnumerateArray()); Assert.Equal(JsonValueKind.Null,secondPage.GetProperty("nextCursor").ValueKind);
        await Code(await human.GetAsync(a.Url+listUrl+"&freshness=NeverReported&cursor="+Uri.EscapeDataString(cursor)),400,"INVALID_REQUEST");
        await Code(await human.GetAsync(a.Url+listUrl+"&cursor="+Uri.EscapeDataString("broken-"+cursor)),400,"INVALID_REQUEST");
        var before=await Ok(await human.GetAsync(a.Url+"/api/v1/manage/instances/"+id)); Assert.Equal("NeverReported",before.GetProperty("freshness").GetString()); Assert.Equal(JsonValueKind.Null,before.GetProperty("lastSnapshot").ValueKind);
        var context=await Ok(await Machine(machine,b.Url,"/api/v1/client/context",instanceBearer)); Assert.Equal(id,context.GetProperty("instanceId").GetGuid()); Assert.Equal(sid,context.GetProperty("softwareId").GetGuid()); Assert.Equal(0,context.GetProperty("currentEpoch").GetInt64());
        await Code(await Machine(machine,a.Url,"/api/v1/client/report-streams",instanceBearer,new { expectedEpoch=0 }),400,"INVALID_REQUEST");
        var streamKey=Guid.NewGuid(); await Ok(await Machine(machine,a.Url,"/api/v1/client/report-streams",instanceBearer,new { expectedEpoch=0 },streamKey)); await Ok(await Machine(machine,b.Url,"/api/v1/client/report-streams",instanceBearer,new { expectedEpoch=0 },streamKey));
        var report=InstanceFixture.Report(); var result=await Ok(await Machine(machine,a.Url,"/api/v1/client/status-reports",instanceBearer,report)); var duplicate=await Ok(await Machine(machine,b.Url,"/api/v1/client/status-reports",instanceBearer,report)); Assert.Equal(result.GetProperty("lastAcceptedAt").GetString(),duplicate.GetProperty("lastAcceptedAt").GetString());
        var view=await Ok(await human.GetAsync(b.Url+"/api/v1/manage/instances/"+id)); Assert.Equal("Fresh",view.GetProperty("freshness").GetString()); Assert.Equal("接入设备",view.GetProperty("deviceName").GetString()); Assert.Equal("1.2.3",view.GetProperty("lastSnapshot").GetProperty("installedVersion").GetString());
        var inventory=await Ok(await human.GetAsync(a.Url+$"/api/v1/manage/devices/{did}/software-inventory"));
        var inventoryItems=inventory.GetProperty("items").EnumerateArray().ToArray(); Assert.Equal(2,inventoryItems.Length);
        var reported=inventoryItems.Single(x=>x.GetProperty("instance").GetProperty("id").GetGuid()==id);
        Assert.Equal("1.2.3",reported.GetProperty("instance").GetProperty("lastSnapshot").GetProperty("installedVersion").GetString());
        Assert.Single(inventoryItems,x=>x.GetProperty("instance").GetProperty("freshness").GetString()=="NeverReported");
        await Code(await Machine(machine,a.Url,"/api/v1/client/status-reports",instanceBearer,report with { RunningState="Stopped" }),409,"REPORT_CONFLICT");
        await Code(await Machine(machine,a.Url,"/api/v1/client/status-reports",instanceBearer,report with { ReportSeq=2,InstalledReleaseId=Guid.NewGuid() }),404,"RESOURCE_NOT_FOUND");
        await Code(await Machine(machine,a.Url,"/api/v1/client/status-reports",instanceBearer,new { instanceId=Guid.NewGuid() }),400,"UNKNOWN_FIELD");
        await Code(await Machine(machine,a.Url,"/api/v1/manage/instances?softwareId="+sid,instanceBearer),401,"CREDENTIAL_INVALID");
        await Code(await human.GetAsync(a.Url+"/api/v1/client/context"),401,"CREDENTIAL_INVALID");
        var grantRow=Assert.Single((await Ok(await human.GetAsync(a.Url+"/api/v1/manage/enrollment-grants?softwareId="+sid))).GetProperty("items").EnumerateArray());
        await Ok(await Write(human,a.Url,"enrollment-grants/"+Id(grant)+"/revoke",new { expectedRevision=grantRow.GetProperty("revision").GetInt64(),reason="撤销新登记" },session));
        var replay=await Ok(await Machine(machine,b.Url,"/api/v1/enrollment/instances",bearer,body,key),201); Assert.Equal(id,replay.GetProperty("instanceId").GetGuid());
        var recoverySecret=InstanceFixture.Secret(); var recovery=await Ok(await Write(human,a.Url,$"instances/{id}/recovery-grants",new { expiresAt=DateTimeOffset.UtcNow.AddHours(1),secretMaterial=recoverySecret,reason="HTTP 恢复许可" },session),201);
        var newSecret=InstanceFixture.Secret(); var recovered=await Ok(await Machine(machine,b.Url,"/api/v1/enrollment/recoveries",Id(recovery)+"."+recoverySecret,new { secretMaterial=newSecret },Guid.NewGuid())); Assert.Equal(id,recovered.GetProperty("instanceId").GetGuid());
        await Code(await Machine(machine,a.Url,"/api/v1/client/context",instanceBearer),401,"CREDENTIAL_INVALID"); await Code(await Machine(machine,a.Url,"/api/v1/enrollment/instances",bearer,body,key),401,"CREDENTIAL_INVALID");
        var newCredential=recovered.GetProperty("credentialId").GetGuid(); var newBearer=newCredential+"."+newSecret;
        var credentials=(await Ok(await human.GetAsync(b.Url+$"/api/v1/manage/instances/{id}/credentials"))).GetProperty("items"); Assert.Equal(2,credentials.GetArrayLength()); var current=credentials.EnumerateArray().Single(x=>Id(x)==newCredential);
        await Ok(await Write(human,a.Url,"credentials/"+newCredential+"/revoke",new { expectedRevision=current.GetProperty("revision").GetInt64(),reason="跨节点吊销" },session)); await Code(await Machine(machine,b.Url,"/api/v1/client/context",newBearer),401,"CREDENTIAL_INVALID");
        foreach(var secret in new[] { grantSecret,instanceSecret,recoverySecret,newSecret }) { Assert.DoesNotContain(secret,view.GetRawText()+credentials.GetRawText()+inventory.GetRawText()); a.AssertRedacted(secret); b.AssertRedacted(secret); }
        Assert.Equal(1,(await Ok(await human.GetAsync(a.Url+$"/api/v1/manage/instances/{id}/version-history"))).GetProperty("items").GetArrayLength());
        var currentUser=await Ok(await human.GetAsync(a.Url+"/api/v1/manage/users/"+session.GetProperty("subjectId").GetGuid()));
        await SiteCatalogHttpTests.Grant(human,a,session,currentUser,Permissions(currentUser).Where(p=>p.Operation!="instance.read").ToArray());
        await Code(await human.GetAsync(b.Url+listUrl+"&cursor="+Uri.EscapeDataString(cursor)),400,"INVALID_REQUEST");
        await Code(await human.GetAsync(b.Url+listUrl),404,"RESOURCE_NOT_FOUND");
    }
    [Fact]
    public async Task MachineProtocolRejectsMissingKeyWrongMappingAndHumanWritesWithoutCsrf()
    {
        await using var db=await PersonnelDatabase.CreateAsync(); await using var a=await LocalApi.StartAsync(db.Database,site:new(Guid.NewGuid(),"接入门禁夹具","Asia/Shanghai")); using var human=a.Client(new CookieContainer()); using var machine=a.Client(new CookieContainer()); var session=await SiteCatalogHttpTests.AuthorizeAsync(db,a,human);
        await Code(await human.PostAsJsonAsync(a.Url+"/api/v1/manage/enrollment-grants",new { }),403,"PERMISSION_DENIED");
        await Code(await Machine(machine,a.Url,"/api/v1/enrollment/instances","bad-secret",new { }),401,"CREDENTIAL_INVALID");
        await Code(await human.GetAsync(a.Url+"/api/v1/client/context"),401,"CREDENTIAL_INVALID");
        var software=await Ok(await Write(human,a.Url,"software",new { code="GATE-S",name="夹具",category="UpperComputer" },session),201); var sid=Id(software);
        await Code(await Write(human,a.Url,"enrollment-grants",new { softwareId=sid,deviceIds=new[] {Guid.NewGuid()},expiresAt=DateTimeOffset.UtcNow.AddHours(1),maxInstances=1,secretMaterial=InstanceFixture.Secret(),reason="无接入权限" },session),404,"RESOURCE_NOT_FOUND");
        var user=await Ok(await human.GetAsync(a.Url+"/api/v1/manage/users/"+session.GetProperty("subjectId").GetGuid())); await SiteCatalogHttpTests.Grant(human,a,session,user,Permissions(user).Append(new(sid,"enrollment.manage")).ToArray());
        await Code(await Write(human,a.Url,"enrollment-grants",new { softwareId=sid,deviceIds=new[] {Guid.NewGuid()},expiresAt=DateTimeOffset.UtcNow.AddHours(1),maxInstances=1,secretMaterial=InstanceFixture.Secret(),reason="错误设备" },session),404,"RESOURCE_NOT_FOUND");
        var process=await Ok(await Write(human,a.Url,"processes",new { code="GATE-P",name="门禁工序" },session),201);
        var device=await Ok(await Write(human,a.Url,"devices",new { processId=Id(process),deviceNo="GATE-D",name="门禁设备" },session),201);
        await Ok(await Write(human,a.Url,$"devices/{Id(device)}/software-bindings",new { softwareId=sid,reason="显式门禁映射" },session),201);
        await Code(await Write(human,a.Url,"enrollment-grants",new { softwareId=sid,deviceIds=new[] {Id(device)},expiresAt=DateTimeOffset.UtcNow.AddHours(1),maxInstances=1,secretMaterial=InstanceFixture.Secret(),reason="缺少签发上限配置" },session),503,"CONFIGURATION_INVALID");
        Assert.Equal(0,await db.CountAsync("iam.enrollment_grants")); Assert.Equal(0,await db.CountAsync("ins.instances"));
    }
    internal static PermissionView[] Permissions(JsonElement u)=>u.GetProperty("permissions").EnumerateArray().Select(x=>new PermissionView(x.GetProperty("softwareId").ValueKind==JsonValueKind.Null?null:x.GetProperty("softwareId").GetGuid(),x.GetProperty("operation").GetString()!)).ToArray();
    internal static Guid Id(JsonElement x)=>x.GetProperty("id").GetGuid();
    internal static async Task<JsonElement> Ok(HttpResponseMessage response,int status=200)
    { using(response) { Assert.Equal(status,(int)response.StatusCode); using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return d.RootElement.Clone(); } }
    internal static async Task Code(HttpResponseMessage response,int status,string code)
    { using(response) { Assert.Equal(status,(int)response.StatusCode); using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(code,d.RootElement.GetProperty("code").GetString()); } }
    internal static Task<HttpResponseMessage> Write(HttpClient client,string url,string path,object body,JsonElement session,Guid? key=null,HttpMethod? method=null)
    { var message=new HttpRequestMessage(method??HttpMethod.Post,url+"/api/v1/manage/"+path) { Content=JsonContent.Create(body) }; message.Headers.Add("X-CSRF-TOKEN",session.GetProperty("csrfToken").GetString()); message.Headers.Add("Idempotency-Key",(key??Guid.NewGuid()).ToString()); return client.SendAsync(message); }
    internal static Task<HttpResponseMessage> Machine(HttpClient client,string url,string path,string bearer,object? body=null,Guid? key=null)
    { var message=new HttpRequestMessage(body is null?HttpMethod.Get:HttpMethod.Post,url+path); if(body is not null) message.Content=JsonContent.Create(body); message.Headers.Add("Authorization","Bearer "+bearer); if(key is not null) message.Headers.Add("Idempotency-Key",key.ToString()); return client.SendAsync(message); }
}
