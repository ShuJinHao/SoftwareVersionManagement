using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Tasks;
using Xunit;
using static Svm.FrameworkTests.DeploymentWorkflowTests;
using static Svm.FrameworkTests.InstanceAccessTests;

namespace Svm.FrameworkTests;
[Trait("Category","Business")]
public sealed class RoleWorkflowTests
{
    [Fact]
    public async Task EnrollmentAndInstanceQueriesRespectDeviceRangeWhileIssuedCredentialSurvivesRoleRemoval()
    {
        await using var f=await InstanceFixture.CreateAsync();var other=(await f.Site.DeviceAsync("OTHER","OTHER")).Device;
        await f.Site.SendAsync(new CreateBindingCommand(Guid.NewGuid(),other.Id,f.SoftwareId,"跨设备共用软件"));
        var role=await Role(f.Site,["asset.read","software.read","instance.read","instance.manage","enrollment.manage"]);
        var person=await f.Site.PersonAsync([]);await Assign(f.Site,person,role,[new("Device",f.Device.Id)]);
        var position=await f.PositionAsync();var secret=InstanceFixture.Secret();
        var grant=(await f.SendAsync(new CreateEnrollmentGrantCommand(Guid.NewGuid(),f.SoftwareId,[f.Device.Id],f.Clock.GetUtcNow().AddHours(1),1,secret,"范围内许可",position.Id,position.Revision),human:person)).Value;
        var deniedPosition=await f.PositionAsync(deviceId:other.Id);
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(new CreateEnrollmentGrantCommand(Guid.NewGuid(),f.SoftwareId,[other.Id],f.Clock.GetUtcNow().AddHours(1),1,InstanceFixture.Secret(),"不属于负责设备",deniedPosition.Id,deniedPosition.Revision),human:person));
        var machineSecret=InstanceFixture.Secret();var registered=(await f.SendAsync(new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,machineSecret),new(ActorKind.EnrollmentGrant,grant.Id,secret))).Value;
        var proof=new AccessProof(ActorKind.Instance,registered.CredentialId,machineSecret);
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),registered.StreamEpoch),proof,registered.InstanceId);
        Assert.Equal(registered.InstanceId,Assert.Single((await f.SendAsync(new ListInstancesQuery(new(new(f.SoftwareId))),human:person)).Items).Id);
        var current=await f.Site.SendAsync(new GetAuthorizationQuery(person.SubjectId));
        await f.Site.SendAsync(new ReplaceAuthorizationCommand(Guid.NewGuid(),person.SubjectId,current.Revision,[],[],"撤销现场人员职责"));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(new GetInstanceQuery(registered.InstanceId),human:person));
        Assert.True((await f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report()),proof,registered.InstanceId)).Value.Applied);
        Assert.Null(Assert.Single((await f.SendAsync(new ListInstanceCredentialsQuery(registered.InstanceId))).Items).RevokedAt);
    }

    [Fact]
    public async Task ScopedTargetsCountsWholeControlsAndDownloadsUseCurrentAuthority()
    {
        await using var f=await TaskFixture.CreateAsync();var release=await Formal(f);var a=await Enroll(f);var site=f.Instances.Site;
        var other=(await site.DeviceAsync("OTHER","OTHER")).Device;var b=await EnrollOn(f,other);
        var role=await Role(site,["asset.read","software.read","instance.read","deployment.create","deployment.control"]);
        var person=await site.PersonAsync([]);await Assign(site,person,role,[new("Device",f.Instances.Device.Id)]);
        var s=(await f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Explicit")),person:person)).Value;
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(new PutTargetChunkCommand(s.Id,0,[a.Id,b.Id]),person:person));
        Assert.Equal(0,(await f.SendAsync(new GetTargetSelectionQuery(s.Id),person:person)).MemberCount);
        var filter=await f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Filter",new(f.SoftwareId))),person:person);
        await Accept(f,filter.WorkId!.Value);var lease=(await f.SendAsync(new ClaimTaskWorkCommand(filter.WorkId.Value,Guid.NewGuid()),work:filter.WorkId)).Value!;
        await f.SendAsync(new MaterializeTaskTargetsCommand(lease),work:filter.WorkId);
        Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection,$"SELECT count(*) FROM tsk.target_members WHERE \"SelectionId\"='{filter.Value.Id}' AND \"InstanceId\"='{b.Id}'"));
        var d=await Deployment(f,release.Id,[a.Id,b.Id]);await Prepare(f,d.WorkId!.Value);await Tick(f,d.Value.Id);
        var visible=await f.SendAsync(new GetDeploymentQuery(d.Value.Id),person:person);
        Assert.Equal(1,visible.SelectedCount);Assert.Equal(1,visible.AcceptedCount);Assert.Equal(1,visible.ResultCounts.Unfinished);Assert.Empty(visible.EffectiveOperations);
        var task=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)),person:person)).Items);Assert.Equal(a.Id,task.InstanceId);
        Assert.Contains("deployment.control",task.EffectiveOperations);
        Assert.Equal(a.Id,Assert.Single((await f.SendAsync(new ListDeploymentTargetsQuery(d.Value.Id),person:person)).Items).InstanceId);
        Assert.Equal(1,Assert.Single((await f.SendAsync(new GetDeploymentBatchesQuery(d.Value.Id),person:person)).Items).TaskCount);
        Assert.Equal(1,(await f.SendAsync(new GetTaskWorkQuery(d.WorkId.Value),person:person)).ProcessedItems);
        await Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(new ControlDeploymentCommand(Guid.NewGuid(),visible.Id,visible.Revision,"Pause","范围不足"),person:person));
        await f.SendAsync(new ControlInstanceTaskCommand(Guid.NewGuid(),task.Id,task.Revision,"Defer","仅暂缓自己负责实例"),person:person);
        Assert.Equal(release.Id,(await f.Packages.SendAsync(new GetReleaseQuery(release.Id),person:person)).Id);
        var download=new AuthorizeDownloadCommand(release.PackageId,Guid.NewGuid(),"node-a",Guid.NewGuid(),false,"node-a");
        await f.Packages.SendAsync(download,person:person,work:release.PackageId,role:"Gateway");
        await Rejected(RequestFailure.ResourceNotFound,()=>f.Packages.SendAsync(new DisableReleaseCommand(Guid.NewGuid(),release.Id,release.Revision,"现场权限不能停用产品"),person:person));
        var auth=await site.SendAsync(new GetAuthorizationQuery(person.SubjectId));await site.SendAsync(new ReplaceAuthorizationCommand(Guid.NewGuid(),person.SubjectId,auth.Revision,[],[],"撤销职责"));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.Packages.SendAsync(download with {RequestId=Guid.NewGuid()},person:person,work:release.PackageId,role:"Gateway"));
    }

    [Fact]
    public async Task ScopeRemovalPreventsNewPermitPausesDeploymentAndRetainsAuthorizedReceipts()
    {
        await using var f=await TaskFixture.CreateAsync();var release=await Formal(f);var a=await Enroll(f);var b=await Enroll(f);var site=f.Instances.Site;
        var role=await Role(site,["asset.read","software.read","instance.read","deployment.create","deployment.control"]);var person=await site.PersonAsync([]);
        await Assign(site,person,role,[new("Process",f.Instances.Device.ProcessId)]);
        var s=(await f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Explicit")),person:person)).Value;
        s=(await f.SendAsync(new PutTargetChunkCommand(s.Id,0,[a.Id,b.Id]),person:person)).Value;
        await f.SendAsync(new SealTargetSelectionCommand(Guid.NewGuid(),s.Id,s.Revision,1,2),person:person);
        var now=f.Instances.Clock.GetUtcNow();var d=await f.SendAsync(new CreateDeploymentCommand(Guid.NewGuid(),new(f.SoftwareId,s.Id,"Update",release.Id,new(now.AddSeconds(-1),now.AddHours(1)),"现场角色投放")),person:person);
        await Prepare(f,d.WorkId!.Value);await Tick(f,d.Value.Id);
        var tasks=(await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items;
        var ta=tasks.Single(t=>t.InstanceId==a.Id);var tb=tasks.Single(t=>t.InstanceId==b.Id);
        var aa=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),ta.Id),access:a.Proof,instance:a.Id)).Value;
        var ab=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),tb.Id),access:b.Proof,instance:b.Id)).Value;
        var report=InstanceFixture.Report(sequence:2,version:null) with {InstallationState="NotInstalled"};
        await f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),aa,new(report,new(PackageFixture.Input.Sha256,true))),access:a.Proof,instance:a.Id);
        var auth=await site.SendAsync(new GetAuthorizationQuery(person.SubjectId));await site.SendAsync(new ReplaceAuthorizationCommand(Guid.NewGuid(),person.SubjectId,auth.Revision,[],[],"撤销负责范围"));
        await Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),ab,new(report,new(PackageFixture.Input.Sha256,true))),access:b.Proof,instance:b.Id));
        await Tick(f,d.Value.Id);Assert.Contains((await f.SendAsync(new GetDeploymentQuery(d.Value.Id))).PauseReasons,x=>x.Code=="AuthorizationChanged");
        Assert.Equal(1,(await f.SendAsync(new GetInstanceQuery(b.Id))).LastSnapshot!.ReportSeq);
        Assert.True((await f.SendAsync(new SubmitTaskReceiptCommand(aa,new(Guid.NewGuid(),3,"Terminal",Result:"Succeeded")),access:a.Proof,instance:a.Id)).Value.Applied);
    }

    private static async Task<RoleView> Role(SiteCatalogFixture site,string[] operations)=>(await site.SendAsync(new CreateRoleCommand(Guid.NewGuid(),"现场角色夹具",operations,"授权链路验证"))).Value;
    private static async Task Assign(SiteCatalogFixture site,SessionProof person,RoleView role,AccessScope[] scopes)
    {var auth=await site.SendAsync(new GetAuthorizationQuery(person.SubjectId));await site.SendAsync(new ReplaceAuthorizationCommand(Guid.NewGuid(),person.SubjectId,auth.Revision,[new(role.Id,scopes)],[],"现场范围夹具"));}
    private static async Task<(Guid Id,AccessProof Proof)> EnrollOn(TaskFixture f,DeviceView device)
    {
        await f.Instances.Site.SendAsync(new CreateBindingCommand(Guid.NewGuid(),device.Id,f.SoftwareId,"跨工序共享"));var p=await f.Instances.PositionAsync(deviceId:device.Id);
        var secret=InstanceFixture.Secret();var g=(await f.Instances.SendAsync(new CreateEnrollmentGrantCommand(Guid.NewGuid(),f.SoftwareId,[device.Id],f.Instances.Clock.GetUtcNow().AddHours(1),1,secret,"夹具许可",p.Id,p.Revision))).Value;
        var machine=InstanceFixture.Secret();var r=(await f.Instances.SendAsync(new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),device.Id,machine),new(ActorKind.EnrollmentGrant,g.Id,secret))).Value;
        var proof=new AccessProof(ActorKind.Instance,r.CredentialId,machine);await f.Instances.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),r.StreamEpoch),proof,r.InstanceId);
        await f.Instances.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report(version:null) with {InstallationState="NotInstalled"}),proof,r.InstanceId);return(r.InstanceId,proof);
    }
}
