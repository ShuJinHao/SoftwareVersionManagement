using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Svm.FrameworkTests.InstanceHttpTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class RoleScopeHttpTests
{
    [Fact]
    public async Task HttpsRoleManagementUsesCurrentAuthorizationCsrfStrictFieldsAndRevisionBoundCursors()
    {
        await using var db=await PersonnelDatabase.CreateAsync();
        var site=new Svm.Services.Contracts.Catalog.SiteCatalogOptions(Guid.NewGuid(),"角色 HTTP 虚构厂区","Asia/Shanghai");
        await using var a=await LocalApi.StartAsync(db.Database,site:site);
        await using var b=await LocalApi.StartAsync(db.Database,site:site);
        using var admin=a.Client(new CookieContainer());var session=await SiteCatalogHttpTests.AuthorizeAsync(db,a,admin);
        var input=new {name="虚构授权管理员",operations=new[]{"identity.manage"},reason="角色 HTTP 测试"};
        await Code(await admin.PostAsJsonAsync(a.Url+"/api/v1/manage/roles",input),403,"PERMISSION_DENIED");
        await Code(await Write(admin,a.Url,"roles",new { input.name,input.operations,input.reason,subjectId=Guid.NewGuid() },session),400,"UNKNOWN_FIELD");
        await Code(await Write(admin,a.Url,"roles",new {name="未开放能力",operations=new[]{"task.rollback"},reason="拒绝"},session),422,"VALIDATION_FAILED");
        var key=Guid.NewGuid();var role=await Ok(await Write(admin,a.Url,"roles",input,session,key),201);
        Assert.Equal(Id(role),Id(await Ok(await Write(admin,b.Url,"roles",input,session,key),201)));
        await Code(await Write(admin,a.Url,"roles/"+Id(role),new { name=input.name,operations=input.operations,isEnabled=true,reason="无修订"},session,method:HttpMethod.Patch),400,"REVISION_REQUIRED");
        var secret=Guid.NewGuid().ToString("N");
        var person=await Ok(await Write(admin,a.Url,"users",new{employeeNo="ROLE-HTTP",displayName="角色管理员夹具",temporaryPassword=secret},session),201);
        var target=Id(person);var authorizationPath="subjects/"+target+"/authorization";
        var assigned=await Ok(await Write(admin,a.Url,authorizationPath,new {expectedRevision=1,reason="全厂管理职责",assignments=new[]{new{roleId=Id(role),scopes=new[]{new{kind="Site",resourceId=(Guid?)null}}}},directPermissions=Array.Empty<object>()},session,method:HttpMethod.Put));
        Assert.Empty(assigned.GetProperty("directPermissions").EnumerateArray());
        using var member=b.Client(new CookieContainer());var memberSession=await SiteCatalogHttpTests.LoginAndChange(member,b.Url,"ROLE-HTTP",secret);
        Assert.Empty(memberSession.GetProperty("permissions").EnumerateArray());
        Assert.Single(memberSession.GetProperty("authorization").GetProperty("roles").EnumerateArray());
        Assert.Equal(target,(await Ok(await member.GetAsync(a.Url+"/api/v1/session/authorization"))).GetProperty("subjectId").GetGuid());
        var first=await Ok(await member.GetAsync(a.Url+"/api/v1/manage/roles?pageSize=1"));var cursor=Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
        Assert.Single((await Ok(await member.GetAsync(b.Url+"/api/v1/manage/roles?pageSize=1&cursor="+cursor))).GetProperty("items").EnumerateArray());
        await Code(await member.GetAsync(a.Url+"/api/v1/manage/roles?pageSize=2&cursor="+cursor),400,"INVALID_REQUEST");
        await Code(await admin.GetAsync(a.Url+"/api/v1/manage/roles?pageSize=1&cursor="+cursor),400,"INVALID_REQUEST");
        var oldRevision=(await Ok(await member.GetAsync(b.Url+"/api/v1/session/authorization"))).GetProperty("revision").GetInt64();
        var changed=await Ok(await Write(admin,a.Url,"roles/"+Id(role),new {expectedRevision=role.GetProperty("revision").GetInt64(),name="统一更名",operations=input.operations,isEnabled=true,reason="集中生效"},session,method:HttpMethod.Patch));
        var refreshed=await Ok(await member.GetAsync(b.Url+"/api/v1/session/authorization"));Assert.True(refreshed.GetProperty("revision").GetInt64()>oldRevision);
        await Code(await member.GetAsync(b.Url+"/api/v1/manage/roles?pageSize=1&cursor="+cursor),400,"INVALID_REQUEST");
        var ownKey=Guid.NewGuid();var ownInput=new{name="由角色授权创建",operations=new[]{"asset.read"},reason="角色权限裁决"};
        await Ok(await Write(member,b.Url,"roles",ownInput,memberSession,ownKey),201);
        await Ok(await Write(admin,a.Url,"roles/"+Id(role),new{expectedRevision=changed.GetProperty("revision").GetInt64(),name="统一更名",operations=input.operations,isEnabled=false,reason="撤销角色管理职责"},session,method:HttpMethod.Patch));
        await Code(await Write(member,b.Url,"roles",ownInput,memberSession,ownKey),403,"PERMISSION_DENIED");
        await Code(await member.GetAsync(b.Url+"/api/v1/manage/users"),403,"PERMISSION_DENIED");
        Assert.False(Assert.Single((await Ok(await member.GetAsync(a.Url+"/api/v1/session/authorization"))).GetProperty("roles").EnumerateArray()).GetProperty("isEnabled").GetBoolean());
        a.AssertRedacted(secret);b.AssertRedacted(secret);a.AssertRedacted(session.GetProperty("csrfToken").GetString()!);
    }

    [Fact]
    public async Task ScopedHttpsCandidatesAndWorkspaceCountsFollowRoleEditsWithoutChangingDirectPermissions()
    {
        await using var db=await PersonnelDatabase.CreateAsync();
        await using var api=await LocalApi.StartAsync(db.Database,site:new(Guid.NewGuid(),"范围 HTTP 夹具","Asia/Shanghai"));
        using var admin=api.Client(new CookieContainer());var session=await SiteCatalogHttpTests.AuthorizeAsync(db,api,admin);
        var p=await Ok(await Write(admin,api.Url,"processes",new{name="授权工序"},session),201);
        var a=await Ok(await Write(admin,api.Url,"devices",new{processId=Id(p),name="授权设备"},session),201);
        var b=await Ok(await Write(admin,api.Url,"devices",new{processId=Id(p),name="另一设备"},session),201);
        var role=await Ok(await Write(admin,api.Url,"roles",new{name="单设备只读",operations=new[]{"asset.read","software.read","instance.read"},reason="最小现场范围"},session),201);
        var secret=Guid.NewGuid().ToString("N");var user=await Ok(await Write(admin,api.Url,"users",new{employeeNo="SCOPE-HTTP",displayName="范围人员",temporaryPassword=secret},session),201);
        await Code(await Write(admin,api.Url,"subjects/"+Id(user)+"/authorization",new{expectedRevision=1,reason="正文不能声明名称",assignments=new[]{new{roleId=Id(role),scopes=new[]{new{kind="Device",resourceId=Id(a),name="伪造名称"}}}},directPermissions=Array.Empty<object>()},session,method:HttpMethod.Put),400,"UNKNOWN_FIELD");
        await Ok(await Write(admin,api.Url,"subjects/"+Id(user)+"/authorization",new{expectedRevision=1,reason="仅指定设备",assignments=new[]{new{roleId=Id(role),scopes=new[]{new{kind="Device",resourceId=Id(a)}}}},directPermissions=Array.Empty<object>()},session,method:HttpMethod.Put));
        using var person=api.Client(new CookieContainer());await SiteCatalogHttpTests.LoginAndChange(person,api.Url,"SCOPE-HTTP",secret);
        var view=await Ok(await person.GetAsync(api.Url+"/api/v1/manage/workspace/devices"));Assert.Equal(1,view.GetProperty("totalCount").GetInt64());Assert.Equal(Id(a),Assert.Single(view.GetProperty("items").EnumerateArray()).GetProperty("device").GetProperty("id").GetGuid());
        await Code(await person.GetAsync(api.Url+"/api/v1/manage/devices/"+Id(b)),404,"RESOURCE_NOT_FOUND");
        await Code(await person.GetAsync(api.Url+"/api/v1/manage/permission-options?kind=Device"),403,"PERMISSION_DENIED");
        foreach(var kind in new[]{"Role","Process","Device","Software"})
            await Ok(await admin.GetAsync(api.Url+"/api/v1/manage/permission-options?kind="+kind+"&pageSize=1"));
        Assert.Empty((await Ok(await admin.GetAsync(api.Url+"/api/v1/manage/users/"+Id(user)))).GetProperty("permissions").EnumerateArray());
        api.AssertRedacted(secret);
    }
}
