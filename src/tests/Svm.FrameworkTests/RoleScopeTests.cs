using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Xunit;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class RoleScopeTests
{
    [Fact]
    public async Task MigrationRetainsLegacyGrantsSeedsOnlyUnassignedRolesAndMatchesModel()
    {
        await using var db = new PersistenceDatabase(); await db.CreateAsync(false);
        await using (var c = Context(db)) await c.GetService<IMigrator>().MigrateAsync("20261021000100_HardwareWorkspace");
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection,"""
            INSERT INTO iam.permission_catalog ("Operation","Global") VALUES ('identity.manage',true) ON CONFLICT DO NOTHING;
            INSERT INTO iam.users ("Id","EmployeeNo","DisplayName","PasswordHash","IsEnabled","MustChangePassword")
              VALUES ('11111111-1111-4111-8111-111111111111','LEGACY-ROLE-FIXTURE','旧权限夹具','not-a-login-hash',true,false);
            INSERT INTO iam.subject_guards VALUES ('11111111-1111-4111-8111-111111111111',7);
            INSERT INTO iam.permissions VALUES ('22222222-2222-4222-8222-222222222222','11111111-1111-4111-8111-111111111111',NULL,'identity.manage');
            """);
        var digest=await PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection,"SELECT md5(string_agg(to_jsonb(p)::text,'|' ORDER BY p.\"Id\")) FROM iam.permissions p");
        var before = await PersistenceDatabase.ScalarAsync<long>(db.MigrationConnection,"SELECT count(*) FROM iam.permissions");
        var result = await db.Runner.ApplyAsync(default); Assert.Equal(14,result.Applied.Count);
        Assert.Equal(before,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM iam.permissions"));
        Assert.Equal(digest,await PersistenceDatabase.ScalarAsync<string>(db.ReaderConnection,"SELECT md5(string_agg(to_jsonb(p)::text,'|' ORDER BY p.\"Id\")) FROM iam.permissions p"));
        Assert.True(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection,"SELECT iam.has_global('11111111-1111-4111-8111-111111111111','identity.manage')"));
        Assert.Equal(4,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM iam.roles"));
        Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM iam.role_assignments"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection,"SELECT has_table_privilege(current_user,'iam.roles','DELETE')"));
        await using var model = Context(db);
        var snapshot=model.GetService<IModelRuntimeInitializer>().Initialize(model.GetService<IMigrationsAssembly>().ModelSnapshot!.Model,designTime:true);
        var differences=model.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot.GetRelationalModel(),model.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel());
        Assert.True(differences.Count==0,string.Join(";",differences.Select(x=>x.GetType().Name)));
        await using var check = Context(db); Assert.False(check.Database.HasPendingModelChanges());
        var folder=Path.Combine(OutboxFixture.Root,"artifacts","roles-and-site-scopes"); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder,"roles-and-site-scopes-upgrade.sql"),db.Runner.GenerateScript());
    }
    [Fact]
    public async Task SharedSoftwareVisibilityCountsAndPagesFollowEachAssignedDevice()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var a=await f.DeviceAsync("A","A"); var b=await f.DeviceAsync("B","B");
        var software=(await f.SoftwareAsync()).Value;
        foreach(var d in new[]{a.Device,b.Device}) { var h=(await f.SendAsync(new CreateHardwareCommand(Guid.NewGuid(),d.Id,"IPC","夹具工控机",null,null))).Value; await f.SendAsync(new CreateHardwareBindingCommand(Guid.NewGuid(),h.Id,software.Id,null,"夹具关联")); }
        var role=await Role(f,"现场只读",["asset.read","software.read","instance.read"]); var person=await f.PersonAsync([]);
        await Assign(f,person,role,[new("Device",a.Device.Id)]);
        var processes=await f.SendAsync(new ListProcessesQuery(new(new(),50)),person); Assert.Equal(a.Process.Id,Assert.Single(processes.Items).Id);
        var devices=await f.SendAsync(new GetWorkspaceDevicesQuery(new(new(),1)),person); Assert.Equal(1,devices.TotalCount); Assert.Equal(a.Device.Id,Assert.Single(devices.Items).Device.Id);
        var locations=await f.SendAsync(new GetWorkspaceInstallationsQuery(new(new(SoftwareId:software.Id),1)),person); Assert.Equal(1,locations.TotalCount); Assert.Null(locations.Next); Assert.Equal(a.Device.Id,Assert.Single(locations.Items).Device.Id);
        Assert.Equal(software.Id,(await f.SendAsync(new GetSoftwareQuery(software.Id),person)).Id);
        await Denied(()=>f.SendAsync(new GetDeviceQuery(b.Device.Id),person));
        var grants=await f.SendAsync(new GetAuthorizationQuery(person.SubjectId)); Assert.Equal(a.Device.Name,Assert.Single(Assert.Single(grants.Roles).Scopes).Name);
    }
    [Fact]
    public async Task OperationScopePairsDoNotBorrowAnotherRolesRangeAndProcessContainsFutureDevices()
    {
        await using var f=await SiteCatalogFixture.CreateAsync(); var a=await f.DeviceAsync("A","A"); var b=await f.DeviceAsync("B","B");
        var edit=await Role(f,"工序维护",["asset.read","asset.create","asset.manage"]); var read=await Role(f,"设备查看",["asset.read"]); var person=await f.PersonAsync([]);
        await Assign(f,person,edit,[new("Process",a.Process.Id)],new RoleAssignmentInput(read.Id,[new("Device",b.Device.Id)]));
        Assert.Equal(2,(await f.SendAsync(new ListDevicesQuery(new(new(),50)),person)).Items.Count);
        await f.SendAsync(new UpdateDeviceCommand(Guid.NewGuid(),a.Device.Id,a.Device.Revision,null,"授权内改名","范围验证"),person);
        await Denied(()=>f.SendAsync(new UpdateDeviceCommand(Guid.NewGuid(),b.Device.Id,b.Device.Revision,null,"不应改变","范围验证"),person));
        var added=(await f.SendAsync(new CreateDeviceCommand(Guid.NewGuid(),a.Process.Id,null,"新设备自动继承"),person)).Value;
        Assert.Equal(3,(await f.SendAsync(new ListDevicesQuery(new(new(),50)),person)).Items.Count);
        await Denied(()=>f.SendAsync(new CreateProcessCommand(Guid.NewGuid(),null,"无全厂创建权"),person));
        await Denied(()=>f.SendAsync(new UpdateDeviceCommand(Guid.NewGuid(),added.Id,added.Revision,b.Process.Id,null,"目标工序无维护权"),person));
    }
    [Fact]
    public async Task NewSoftwareAssociationIsVisibleWithoutCreatorGrantsOrProductPublishingRights()
    {
        await using var f=await SiteCatalogFixture.CreateAsync(); var d=(await f.DeviceAsync()).Device;
        var h=(await f.SendAsync(new CreateHardwareCommand(Guid.NewGuid(),d.Id,"IPC","夹具",null,null))).Value;
        var engineer=await Role(f,"现场工程师",["asset.read","software.read","instance.read","software.create","binding.manage","release.upload"]);
        var person=await f.PersonAsync([]); await Assign(f,person,engineer,[new("Device",d.Id)]);
        var bound=(await f.SendAsync(new CreateHardwareBindingCommand(Guid.NewGuid(),h.Id,null,new("新产品","Vision",null),"现场创建"),person)).Value;
        Assert.Empty((await f.UserAsync(person.SubjectId)).Permissions);
        Assert.Equal(bound.SoftwareId,(await f.SendAsync(new GetSoftwareQuery(bound.SoftwareId),person)).Id);
        var auth=await f.SendAsync(new GetAuthorizationQuery(person.SubjectId)); Assert.False(auth.Product("release.upload",bound.SoftwareId)); Assert.False(auth.Product("release.publish",bound.SoftwareId));
        await Denied(()=>f.SendAsync(new UpdateSoftwareCommand(Guid.NewGuid(),bound.SoftwareId,1,"非法产品修改",null,"仅有现场范围"),person));
    }
    [Fact]
    public async Task RoleEditsAffectAllMembersAdvanceRevisionAndDisableWithoutDeletingAssignments()
    {
        await using var f=await SiteCatalogFixture.CreateAsync(); var d=(await f.DeviceAsync()).Device; var role=await Role(f,"共享角色",["asset.read"]);
        var a=await f.PersonAsync([]); var b=await f.PersonAsync([]); await Assign(f,a,role,[new("Device",d.Id)]); await Assign(f,b,role,[new("Device",d.Id)]);
        var before=await f.SendAsync(new GetAuthorizationQuery(a.SubjectId)); var beforeB=await f.SendAsync(new GetAuthorizationQuery(b.SubjectId));
        var current=await f.SendAsync(new GetRoleQuery(role.Id)); Assert.Equal(2,current.AssignedPeople);
        var edit=new UpdateRoleCommand(Guid.NewGuid(),role.Id,current.Revision,role.Name,true,["asset.read","asset.manage"],"集中修改");
        var updated=await f.SendAsync(edit); Assert.Equal(updated.OperationId,(await f.SendAsync(edit)).OperationId);
        Assert.True((await f.SendAsync(new GetAuthorizationQuery(a.SubjectId))).Revision>before.Revision); Assert.True((await f.SendAsync(new GetAuthorizationQuery(b.SubjectId))).Revision>beforeB.Revision);
        var device=(await f.SendAsync(new UpdateDeviceCommand(Guid.NewGuid(),d.Id,d.Revision,null,"角色统一生效","角色编辑后"),b)).Value;
        await f.SendAsync(new UpdateRoleCommand(Guid.NewGuid(),role.Id,updated.Value.Revision,role.Name,false,updated.Value.Operations,"停用角色"));
        await InstanceAccessTests.Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(new GetDeviceQuery(device.Id),a));
        Assert.Single((await f.SendAsync(new GetAuthorizationQuery(a.SubjectId))).Roles);
    }
    [Fact]
    public async Task LegacyDirectPermissionsSurviveRoleReplacementAndOldEndpointKeepsRoles()
    {
        await using var f=await SiteCatalogFixture.CreateAsync(); var d=(await f.DeviceAsync()).Device; var person=await f.PersonAsync([new(null,"asset.read")]); var role=await Role(f,"设备创建",["asset.create"]);
        await Assign(f,person,role,[new("Device",d.Id)]);
        var auth=await f.SendAsync(new GetAuthorizationQuery(person.SubjectId)); Assert.True(auth.Global("asset.read")); Assert.Single(auth.Roles);
        var user=await f.UserAsync(person.SubjectId); await f.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),user.Id,user.Revision,[],"显式收窄旧授权"));
        var after=await f.SendAsync(new GetAuthorizationQuery(person.SubjectId)); Assert.Empty(after.DirectPermissions); Assert.Single(after.Roles);
        var h=(await f.SendAsync(new CreateHardwareCommand(Guid.NewGuid(),d.Id,"PLC","只建台账",null,null),person)).Value; Assert.Equal(d.Id,h.DeviceId);
    }
    [Fact]
    public async Task RoleWritesRollbackAuditAndOperationTogetherAndRejectUnknownScopeReferences()
    {
        await using var f=await SiteCatalogFixture.CreateAsync(); var before=await f.CountAsync("iam.roles"); var audits=await f.CountAsync("aud.events");
        var c=new CreateRoleCommand(Guid.NewGuid(),"回滚角色",["asset.read"],"失败注入");
        await Assert.ThrowsAnyAsync<Exception>(()=>f.SendAsync(c,auditFailure:true)); Assert.Equal(before,await f.CountAsync("iam.roles")); Assert.Equal(audits,await f.CountAsync("aud.events"));
        var result=await f.SendAsync(c); Assert.Equal(result.OperationId,(await f.SendAsync(c)).OperationId);
        var person=await f.PersonAsync([]); await Denied(()=>Assign(f,person,result.Value,[new("Device",Guid.NewGuid())]));
        Assert.Empty((await f.SendAsync(new GetAuthorizationQuery(person.SubjectId))).Roles);
    }
    [Fact]
    public async Task DeviceMoveAdvancesCursorAuthorityAndRechecksOldAndNewRange()
    {
        await using var f=await SiteCatalogFixture.CreateAsync(); var a=await f.DeviceAsync("A","A"); var b=await f.DeviceAsync("B","B"); var person=await f.PersonAsync([]);
        var role=await Role(f,"单工序",["asset.read","asset.manage"]); await Assign(f,person,role,[new("Process",a.Process.Id)]);
        var before=await f.SendAsync(new GetAuthorizationQuery(person.SubjectId)); await Denied(()=>f.SendAsync(new UpdateDeviceCommand(Guid.NewGuid(),a.Device.Id,a.Device.Revision,b.Process.Id,null,"禁止越界移动"),person));
        await f.SendAsync(new UpdateDeviceCommand(Guid.NewGuid(),a.Device.Id,a.Device.Revision,b.Process.Id,null,"管理员移动"));
        Assert.True((await f.SendAsync(new GetAuthorizationQuery(person.SubjectId))).Revision>before.Revision);
        await Denied(()=>f.SendAsync(new GetDeviceQuery(a.Device.Id),person));
    }
    private static Task Denied<T>(Func<Task<T>> action)=>InstanceAccessTests.Rejected(RequestFailure.ResourceNotFound,action);
    [Fact]
    public async Task LastRoleAdministratorSurvivesConcurrentDisablingAndRoleRevocation()
    {
        await using var f=await SiteCatalogFixture.CreateAsync(); var role=await Role(f,"人员管理角色",["identity.manage"]);
        var a=await f.PersonAsync([]); var b=await f.PersonAsync([]);
        await Assign(f,a,role,[new("Site",null)]); await Assign(f,b,role,[new("Site",null)]);
        var root=await f.UserAsync(f.Proof.SubjectId);
        await f.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),root.Id,root.Revision,[],"仅保留角色管理员"));
        var ua=await f.SendAsync(new GetUserQuery(a.SubjectId),a); var ub=await f.SendAsync(new GetUserQuery(b.SubjectId),a);
        async Task<bool> Disable(SessionProof actor,UserView target)
        { try { await f.SendAsync(new UpdateUserCommand(Guid.NewGuid(),target.Id,target.Revision,null,false,"并发停用管理员"),actor);return true; }
          catch(RequestRejectedException e) when(e.Failure is RequestFailure.InvalidState or RequestFailure.AuthenticationRequired or RequestFailure.PermissionDenied){return false;} }
        var disabled=await Task.WhenAll(Disable(a,ub),Disable(b,ua)); Assert.Single(disabled,x=>x);
        var survivor=disabled[0]?a:b; var current=await f.SendAsync(new GetRoleQuery(role.Id),survivor);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new UpdateRoleCommand(Guid.NewGuid(),role.Id,current.Revision,role.Name,false,current.Operations,"不能停用最后管理角色"),survivor));
        var authorization=await f.SendAsync(new GetAuthorizationQuery(survivor.SubjectId),survivor);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new ReplaceAuthorizationCommand(Guid.NewGuid(),survivor.SubjectId,authorization.Revision,[],[],"不能移除最后管理员"),survivor));
    }
    [Fact]
    public async Task LostRoleCommitConfirmationIsQueriedOnceAndCurrentRevocationBlocksReplay()
    {
        await using var f=await SiteCatalogFixture.CreateAsync();var manager=await f.PersonAsync([new(null,"identity.manage")]);var lost=new LostConfirmation();
        var before=await f.CountAsync("iam.roles");var c=new CreateRoleCommand(Guid.NewGuid(),"提交核实角色",["asset.read"],"确认丢失");
        var result=await f.SendAsync(c,manager,lost);Assert.Equal(1,lost.Commits);Assert.Equal(before+1,await f.CountAsync("iam.roles"));
        Assert.Equal(result.OperationId,(await f.SendAsync(c,manager)).OperationId);
        var user=await f.UserAsync(manager.SubjectId);await f.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),user.Id,user.Revision,[],"撤销管理员"));
        await InstanceAccessTests.Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(c,manager));
    }
    private sealed class LostConfirmation : DbTransactionInterceptor
    { internal int Commits; public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token=default) { Commits++;throw new IOException("role fixture commit confirmation lost"); } }
    private static async Task<RoleView> Role(SiteCatalogFixture f,string name,IReadOnlyList<string> operations)=>(await f.SendAsync(new CreateRoleCommand(Guid.NewGuid(),name,operations,"验证角色"))).Value;
    private static async Task<OperationResult<AuthorizationView>> Assign(SiteCatalogFixture f,SessionProof person,RoleView role,IReadOnlyList<AccessScope> scopes,params RoleAssignmentInput[] others)
    {
        var before=await f.SendAsync(new GetAuthorizationQuery(person.SubjectId));
        return await f.SendAsync(new ReplaceAuthorizationCommand(Guid.NewGuid(),person.SubjectId,before.Revision,new[]{new RoleAssignmentInput(role.Id,scopes)}.Concat(others).ToArray(),before.DirectPermissions,"分配职责"));
    }
    private static SvmDbContext Context(PersistenceDatabase db)=>new(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection,p=>p.MigrationsHistoryTable("__EFMigrationsHistory","framework")).Options);
}
