using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Xunit;
using static Svm.FrameworkTests.InstanceAccessTests;

namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class DeploymentWorkflowTests
{
    [Fact]
    public async Task ExplicitSelectionClaimPermitReceiptAndActualVersionAreSeparateFacts()
    {
        await using var f = await TaskFixture.CreateAsync(); var r = await Formal(f);
        var instance = await Enroll(f);
        var d = await Deployment(f,r.Id,[instance.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var tasks = await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id))); var task = Assert.Single(tasks.Items);
        Assert.True(task.State == "Available",System.Text.Json.JsonSerializer.Serialize(new { task, deployment=await f.SendAsync(new GetDeploymentQuery(d.Value.Id)),batches=await f.SendAsync(new GetDeploymentBatchesQuery(d.Value.Id)) }));
        var key = new ClaimInstanceTaskCommand(Guid.NewGuid(),task.Id);
        var claim = await f.SendAsync(key,access:instance.Proof,instance:instance.Id);
        Assert.Equal(claim,await f.SendAsync(key,access:instance.Proof,instance:instance.Id));
        var report = InstanceFixture.Report(sequence:2,version:null) with { InstallationState = "NotInstalled", InstalledVersion = null, InstalledReleaseId = null, InstalledAt = null };
        var start = new StartInstanceTaskCommand(Guid.NewGuid(),claim.Value,new(report,new(PackageFixture.Input.Sha256,true)));
        var permit = await f.SendAsync(start,access:instance.Proof,instance:instance.Id);
        Assert.Equal(r.Id,permit.Value.TargetReleaseId);
        var receipt = new SubmitTaskReceiptCommand(claim.Value,new(Guid.NewGuid(),3,"Terminal",Result:"Succeeded"));
        var received = await f.SendAsync(receipt,access:instance.Proof,instance:instance.Id);
        Assert.True(received.Value.Applied); Assert.Equal(received.Value,(await f.SendAsync(receipt,access:instance.Proof,instance:instance.Id)).Value);
        Assert.Null((await f.SendAsync(new GetInstanceQuery(instance.Id))).LastSnapshot!.InstalledVersion);
        await Tick(f,d.Value.Id); Assert.Equal("Completed",(await f.SendAsync(new GetDeploymentQuery(d.Value.Id))).State);
        Assert.Equal(task.Id,(await f.SendAsync(new GetInstanceQuery(instance.Id))).LatestTaskId);
    }
    [Fact]
    public async Task FilterSelectionUsesOneRestrictedSnapshotAndExplicitChunksKeepNaturalResults()
    {
        await using var f = await TaskFixture.CreateAsync(); var one = await Enroll(f);
        var created = await f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Filter",new(f.SoftwareId))));
        await Accept(f,created.WorkId!.Value); var lease = (await f.SendAsync(new ClaimTaskWorkCommand(created.WorkId.Value,Guid.NewGuid()),work:created.WorkId)).Value!;
        var sealedSelection = (await f.SendAsync(new MaterializeTaskTargetsCommand(lease),work:created.WorkId)).Value;
        Assert.Equal("Sealed",sealedSelection.State); Assert.Equal(1,sealedSelection.MemberCount);
        await Enroll(f); Assert.Equal(1,(await f.SendAsync(new GetTargetSelectionQuery(sealedSelection.Id))).MemberCount);
        var s = (await f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Explicit")))).Value;
        var chunk = new PutTargetChunkCommand(s.Id,0,[one.Id,one.Id]); var first = (await f.SendAsync(chunk)).Value;
        await f.SendAsync(new SealTargetSelectionCommand(Guid.NewGuid(),s.Id,first.Revision,1,1));
        Assert.Equal(first,(await f.SendAsync(chunk)).Value);
        await Rejected(RequestFailure.IdempotencyConflict,()=>f.SendAsync(chunk with { InstanceIds = [Guid.NewGuid()] }));
    }
    [Fact]
    public async Task StartRejectsUnknownOrRollbackAndDoesNotSaveAttachedSnapshot()
    {
        await using var f = await TaskFixture.CreateAsync(); var r = await Formal(f); var i = await Enroll(f);
        var d = await Deployment(f,r.Id,[i.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var t = Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items);
        var a = (await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),t.Id),access:i.Proof,instance:i.Id)).Value;
        var before = await f.Count("ins.installation_evidence");
        var unknown = InstanceFixture.Report(sequence:2,version:null) with { InstallationState = "Unknown", InstalledVersion = null };
        await Rejected(RequestFailure.CurrentVersionUnknown,()=>f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),a,new(unknown,new(PackageFixture.Input.Sha256,true))),access:i.Proof,instance:i.Id));
        await Rejected(RequestFailure.RollbackRequired,()=>f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),a,new(InstanceFixture.Report(sequence:2,version:"9.0.0"),new(PackageFixture.Input.Sha256,true))),access:i.Proof,instance:i.Id));
        Assert.Equal(before,await f.Count("ins.installation_evidence")); Assert.Equal("NotInstalled",(await f.SendAsync(new GetInstanceQuery(i.Id))).LastSnapshot!.InstallationState);
    }
    [Fact]
    public async Task BusyAdmissionAndFirstTerminalPreventConflictingExecution()
    {
        await using var f = await TaskFixture.CreateAsync(); var r = await Formal(f); var i = await Enroll(f);
        var first = await Deployment(f,r.Id,[i.Id]); var second = await Deployment(f,r.Id,[i.Id]);
        await Task.WhenAll(Prepare(f,first.WorkId!.Value),Prepare(f,second.WorkId!.Value));
        var results = (await f.SendAsync(new ListDeploymentTargetsQuery(first.Value.Id))).Items.Concat((await f.SendAsync(new ListDeploymentTargetsQuery(second.Value.Id))).Items).ToArray();
        Assert.Single(results,x=>x.Decision=="Accepted"); Assert.Single(results,x=>x.ReasonCode=="INSTANCE_BUSY");
    }
    [Fact]
    public async Task AuditFailureRollsBackSelectionOutboxAndIdempotencyTogether()
    {
        await using var f = await TaskFixture.CreateAsync(); var count = await f.Count("tsk.target_selections"); var outbox = await OutboxFixture.MessagesAsync(f.Database);
        var error = await Assert.ThrowsAsync<PersistenceException>(()=>f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Filter",new(f.SoftwareId))),auditFailure:true)); Assert.Equal(PersistenceFailure.DependencyUnavailable,error.Failure);
        Assert.Equal(count,await f.Count("tsk.target_selections")); Assert.Equal(outbox,await OutboxFixture.MessagesAsync(f.Database));
    }
    [Fact]
    public async Task MixedExplicitTargetsKeepUnknownIdsAndRejectOutOfScopeWithoutLeakingFacts()
    {
        await using var f=await TaskFixture.CreateAsync(); var release=await Formal(f); var valid=await Enroll(f); var unknown=Guid.NewGuid();
        var other=(await f.Instances.Site.SendAsync(new CreateSoftwareCommand(Guid.NewGuid(),"TASK-OTHER","夹具其他软件","Vision",null))).Value;
        await f.Instances.Site.SendAsync(new CreateBindingCommand(Guid.NewGuid(),f.Instances.Device.Id,other.Id,"夹具第二软件映射"));
        var user=await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),user.Id,user.Revision,user.Permissions.Append(new(other.Id,"enrollment.manage")).ToArray(),"夹具登记第二软件实例"));
        var secret=InstanceFixture.Secret(); var grant=(await f.Instances.SendAsync(new CreateEnrollmentGrantCommand(Guid.NewGuid(),other.Id,[f.Instances.Device.Id],f.Instances.Clock.GetUtcNow().AddHours(1),1,secret,"夹具另一软件登记许可"))).Value;
        var foreign=(await f.Instances.SendAsync(new RegisterInstanceCommand(Guid.NewGuid(),other.Id,Guid.NewGuid(),f.Instances.Device.Id,InstanceFixture.Secret()),new AccessProof(ActorKind.EnrollmentGrant,grant.Id,secret),softwareId:other.Id)).Value;
        var deployment=await Deployment(f,release.Id,[valid.Id,foreign.InstanceId,unknown,valid.Id]);
        await Prepare(f,deployment.WorkId!.Value);
        var view=await f.SendAsync(new GetDeploymentQuery(deployment.Value.Id)); Assert.Equal(3,view.SelectedCount); Assert.Equal(3,view.ProcessedCount); Assert.Equal(1,view.AcceptedCount); Assert.Equal(2,view.RejectedCount);
        var all=new List<AdmissionView>(); Guid? after=null;
        do { var page=await f.SendAsync(new ListDeploymentTargetsQuery(view.Id,1,after)); all.AddRange(page.Items); after=page.Next; } while(after is not null);
        Assert.Equal(3,all.Count); Assert.Equal(3,all.Select(x=>x.InstanceId).Distinct().Count());
        Assert.Equal(valid.Id,Assert.Single(all,x=>x.Decision=="Accepted").InstanceId);
        Assert.All(all.Where(x=>x.Decision=="Rejected"),x=>{ Assert.Equal("OUT_OF_SCOPE",x.ReasonCode); Assert.Null(x.TaskId); });
        Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,view.Id)))).Items);
        var filtered=await f.SendAsync(new ListDeploymentTargetsQuery(view.Id,200,Decision:"Rejected",ReasonCode:"OUT_OF_SCOPE")); Assert.Equal(2,filtered.Items.Count);
        Assert.DoesNotContain(other.Name,System.Text.Json.JsonSerializer.Serialize(filtered));
    }
    [Fact]
    public void ConfigurationRequiresExplicitValidatedLimits()
    {
        Assert.Throws<RequestRejectedException>(()=>new TaskOptions().Validate()); TaskFixture.TaskOptions.Validate();
        Assert.Throws<RequestRejectedException>(()=>(TaskFixture.TaskOptions with { DefaultLatestStartLocalTime = TaskFixture.TaskOptions.DefaultStartLocalTime }).Validate());
    }
    internal static async Task<(Guid Id, AccessProof Proof)> Enroll(TaskFixture f)
    { var e = await f.Instances.EnrollAsync(); await f.Instances.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(), e.Registration.StreamEpoch), e.Proof, e.Registration.InstanceId);
      await f.Instances.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report(version:null) with { InstallationState = "NotInstalled" }), e.Proof,e.Registration.InstanceId);
      return (e.Registration.InstanceId,e.Proof); }
    internal static async Task<ReleaseView> Formal(TaskFixture f)
    { var r = await PublicationScenario.ReadyAsync(f.Packages); var installed = await PublicationScenario.InstallAsync(f.Packages,r); return (await f.Packages.SendAsync(PublicationScenario.Publish(r,installed.Evidence))).Value; }
    internal static async Task<OperationResult<DeploymentView>> Deployment(TaskFixture f,Guid release,Guid[] ids,Guid? retryOf=null)
    { var s = (await f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Explicit")))).Value; var chunks = ids.Chunk(TaskFixture.TaskOptions.SelectionChunkSize!.Value).ToArray();
      for(var index=0;index<chunks.Length;index++) s=(await f.SendAsync(new PutTargetChunkCommand(s.Id,index,chunks[index]))).Value;
      await f.SendAsync(new SealTargetSelectionCommand(Guid.NewGuid(),s.Id,s.Revision,chunks.Length,ids.Distinct().Count())); var now = f.Instances.Clock.GetUtcNow();
      return await f.SendAsync(new CreateDeploymentCommand(Guid.NewGuid(),new(f.SoftwareId,s.Id,"Update",release,new(now.AddSeconds(-1),now.AddHours(1)),"fixture deployment",retryOf))); }
    internal static async Task Accept(TaskFixture f,Guid id)
    { await using var p = f.Provider(); await using var scope = p.CreateAsyncScope(); var tasks = scope.ServiceProvider.GetRequiredService<ITaskWorkflow>(); var w = await tasks.AuthorityAsync(id,false,default);
      await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(),async ct=> { await tasks.AcceptAsync(id,w.DispatchSequence,w.DispatchEventId,ct); return true; },default); }
    internal static async Task Prepare(TaskFixture f,Guid id)
    { await Accept(f,id); for(var n=0;n<20;n++) { var lease = (await f.SendAsync(new ClaimTaskWorkCommand(id,Guid.NewGuid()),work:id)).Value; if(lease is null) return;
      var step = (await f.SendAsync(new AdvanceTaskWorkCommand(lease),work:id)).Value; if(step.State=="Completed") return; } throw new InvalidOperationException("work did not finish"); }
    internal static async Task Tick(TaskFixture f,Guid deployment)
    { f.Instances.Clock.Advance(TimeSpan.FromSeconds(TaskFixture.TaskOptions.PollRetrySeconds!.Value)); await using var p = f.Provider(); await using var scope = p.CreateAsyncScope(); var tasks = scope.ServiceProvider.GetRequiredService<ITaskWorkflow>();
      foreach(var id in await tasks.PendingAsync(100,default)) { var w = await tasks.AuthorityAsync(id,false,default); if(w.Kind!="Schedule" || w.ResourceId!=deployment)continue;
        var l = (await f.SendAsync(new ClaimTaskWorkCommand(id,Guid.NewGuid()),work:id)).Value; if(l is not null) await f.SendAsync(new AdvanceTaskWorkCommand(l),work:id); } }
}
