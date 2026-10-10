using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Identity;
using Xunit;
using static Svm.FrameworkTests.DeploymentWorkflowTests;
using static Svm.FrameworkTests.InstanceAccessTests;

namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class DeploymentControlTests
{
    [Fact]
    public async Task SequencedReceiptsDoNotRegressOrCountTwiceAndFailureRequiresExactReview()
    {
        await using var f=await TaskFixture.CreateAsync(); var r=await Formal(f); var i=await Enroll(f); var j=await Enroll(f); var k=await Enroll(f);
        var d=await Deployment(f,r.Id,[i.Id,j.Id,k.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var all=(await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items;
        var active=all.Where(t=>t.State=="Available").ToArray(); Assert.Equal(2,active.Length);
        var t=active.SingleOrDefault(x=>x.InstanceId==i.Id) ?? active[0]; var proof=t.InstanceId==i.Id ? i.Proof : t.InstanceId==j.Id ? j.Proof : k.Proof;
        var a=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),t.Id),access:proof,instance:t.InstanceId)).Value;
        var ahead=new SubmitTaskReceiptCommand(a,new(Guid.NewGuid(),3,"Progress",Progress:"ReadyToInstall"));
        Assert.True((await f.SendAsync(ahead,access:proof,instance:t.InstanceId)).Value.Applied);
        Assert.False((await f.SendAsync(new SubmitTaskReceiptCommand(a,new(Guid.NewGuid(),2,"Progress",Progress:"Downloading")),access:proof,instance:t.InstanceId)).Value.Applied);
        Assert.Equal("ReadyToInstall",(await f.SendAsync(new GetInstanceTaskQuery(t.Id))).LastReportedProgress);
        var failed=new SubmitTaskReceiptCommand(a,new(Guid.NewGuid(),4,"Terminal",Result:"Failed",FailureCode:"DOWNLOAD_FAILED",Detail:"controlled fixture failure"));
        var original=(await f.SendAsync(failed,access:proof,instance:t.InstanceId)).Value;
        Assert.Equal(original,(await f.SendAsync(failed,access:proof,instance:t.InstanceId)).Value);
        await Rejected(RequestFailure.ReceiptConflict,()=>f.SendAsync(failed with { Input=failed.Input with { Detail="different" } },access:proof,instance:t.InstanceId));
        await Rejected(RequestFailure.ReceiptConflict,()=>f.SendAsync(new SubmitTaskReceiptCommand(a,new(Guid.NewGuid(),5,"Terminal",Result:"NotStarted")),access:proof,instance:t.InstanceId));
        await Tick(f,d.Value.Id); var paused=await f.SendAsync(new GetDeploymentQuery(d.Value.Id)); Assert.Contains(paused.PauseReasons,x=>x.Code=="FailureLimit" && x.Scope=="FutureBatches");
        var batch=Assert.Single((await f.SendAsync(new GetDeploymentBatchesQuery(d.Value.Id))).Items,x=>x.FailedCount==1); Assert.Equal(1,batch.FailureRevision);
        await Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new ControlDeploymentCommand(Guid.NewGuid(),d.Value.Id,paused.Revision,"Resume","review omitted")));
        await Rejected(RequestFailure.RevisionConflict,()=>f.SendAsync(new ControlDeploymentCommand(Guid.NewGuid(),d.Value.Id,paused.Revision,"Resume","stale review",ReviewedFailures:[new(batch.Id,0)])));
        await f.SendAsync(new ControlDeploymentCommand(Guid.NewGuid(),d.Value.Id,paused.Revision,"Resume","failure checked",ReviewedFailures:[new(batch.Id,batch.FailureRevision)]));
        Assert.Equal(1,(await f.SendAsync(new GetDeploymentQuery(d.Value.Id))).ResultCounts.Failed);
    }
    [Fact]
    public async Task TimeoutCloseUnknownRetryAndLateSnapshotRemainIsolated()
    {
        await using var f=await TaskFixture.CreateAsync(); var r=await Formal(f); var i=await Enroll(f); var d=await Deployment(f,r.Id,[i.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var t=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items);
        var a=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),t.Id),access:i.Proof,instance:i.Id)).Value;
        await f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),a,new(NotInstalled(2),new(PackageFixture.Input.Sha256,true))),access:i.Proof,instance:i.Id);
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(31)); await Tick(f,d.Value.Id);
        var paused=await f.SendAsync(new GetDeploymentQuery(d.Value.Id)); Assert.Contains(paused.PauseReasons,x=>x.Code=="ReceiptTimeout");
        t=await f.SendAsync(new GetInstanceTaskQuery(t.Id));
        var closure=new ControlInstanceTaskCommand(Guid.NewGuid(),t.Id,t.Revision,"CloseUnknown","现场确认结束跟踪","夹具核实：无安装进程",true);
        Assert.Equal("Unknown",(await f.SendAsync(closure)).Value.TerminalResult);
        await PublicationScenario.RefreshReplicasAsync(f.Packages,r.PackageId); var retry=await Deployment(f,r.Id,[i.Id],d.Value.Id); await Prepare(f,retry.WorkId!.Value); await Tick(f,retry.Value.Id);
        var current=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,retry.Value.Id)))).Items);
        var late=new SubmitTaskReceiptCommand(a,new(Guid.NewGuid(),7,"Terminal",Result:"Succeeded",StateReport:NotInstalled(3) with { InstallationState="Installed",InstalledReleaseId=r.Id,InstalledVersion=r.Version }));
        var receipt=(await f.SendAsync(late,access:i.Proof,instance:i.Id)).Value; Assert.False(receipt.Applied); Assert.True(receipt.LateAfterClosure); Assert.False(receipt.StateReportApplied);
        Assert.Equal("NotInstalled",(await f.SendAsync(new GetInstanceQuery(i.Id))).LastSnapshot!.InstallationState);
        Assert.Equal("Available",(await f.SendAsync(new GetInstanceTaskQuery(current.Id))).State);
        Assert.Equal(current.Id,(await f.SendAsync(new GetInstanceQuery(i.Id))).LatestTaskId);
        Assert.Equal("ClosedUnknown",(await f.SendAsync(new GetInstanceTaskQuery(t.Id))).State);
    }
    [Fact]
    public async Task PauseWindowPreflightAndRevocationRejectWithoutSavingAttachedReport()
    {
        await using var f=await TaskFixture.CreateAsync(); var r=await Formal(f); var i=await Enroll(f); var d=await Deployment(f,r.Id,[i.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var t=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items); var a=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),t.Id),access:i.Proof,instance:i.Id)).Value;
        var start=new StartInstanceTaskCommand(Guid.NewGuid(),a,new(NotInstalled(2),new(PackageFixture.Input.Sha256,true)));
        var view=await f.SendAsync(new GetDeploymentQuery(d.Value.Id)); var paused=(await f.SendAsync(new ControlDeploymentCommand(Guid.NewGuid(),view.Id,view.Revision,"Pause","hold starts"))).Value;
        await Rejected(RequestFailure.InvalidState,()=>f.SendAsync(start,access:i.Proof,instance:i.Id));
        Assert.Equal(1,(await f.SendAsync(new GetInstanceQuery(i.Id))).LastSnapshot!.ReportSeq);
        await f.SendAsync(new ControlDeploymentCommand(Guid.NewGuid(),view.Id,paused.Revision,"Resume","continue"));
        await Rejected(RequestFailure.ChecksumMismatch,()=>f.SendAsync(start with { Input=start.Input with { Preflight=new(new string('b',64),true) } },access:i.Proof,instance:i.Id));
        var u=await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),u.Id,u.Revision,u.Permissions.Where(p=>p.Operation!="deployment.create").ToArray(),"撤销投放授权"));
        await Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(start,access:i.Proof,instance:i.Id));
        Assert.Equal(1,(await f.SendAsync(new GetInstanceQuery(i.Id))).LastSnapshot!.ReportSeq);
        await Tick(f,view.Id); Assert.Contains((await f.SendAsync(new GetDeploymentQuery(view.Id))).PauseReasons,p=>p.Code=="AuthorizationChanged");
    }
    [Fact]
    public async Task DeferredRescheduleAndCancelUseOriginalPersistentControlWork()
    {
        await using var f=await TaskFixture.CreateAsync(); var r=await Formal(f); var i=await Enroll(f); var d=await Deployment(f,r.Id,[i.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var t=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items);
        t=(await f.SendAsync(new ControlInstanceTaskCommand(Guid.NewGuid(),t.Id,t.Revision,"Defer","fixture defer"))).Value;
        Assert.True(t.IsDeferred); t=(await f.SendAsync(new ControlInstanceTaskCommand(Guid.NewGuid(),t.Id,t.Revision,"Restore","fixture restore"))).Value;
        var v=await f.SendAsync(new GetDeploymentQuery(d.Value.Id)); var now=f.Instances.Clock.GetUtcNow(); var control=new CreateDeploymentControlWorkCommand(Guid.NewGuid(),v.Id,v.Revision,"Reschedule","later",new(now.AddMinutes(10),now.AddMinutes(20)));
        var user=await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        var withoutRead=(await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),user.Id,user.Revision,user.Permissions.Where(p=>p.Operation!="instance.read").ToArray(),"夹具验证控制与查询权限独立"))).Value;
        var work=await f.SendAsync(control); Assert.Equal("Reschedule",work.Value.Kind); Assert.Equal(work.WorkId,work.Value.Id);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(work),System.Text.Json.JsonSerializer.Serialize(await f.SendAsync(control)));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(new GetTaskWorkQuery(work.Value.Id)));
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),user.Id,withoutRead.Revision,user.Permissions,"夹具恢复查询授权"));
        await Prepare(f,work.WorkId!.Value);
        t=await f.SendAsync(new GetInstanceTaskQuery(t.Id)); Assert.Equal(now.AddMinutes(10),t.Window.NotBefore);
        v=await f.SendAsync(new GetDeploymentQuery(v.Id)); Assert.False(v.ControlPending); Assert.Equal("Paused",v.State);
        var cancel=await f.SendAsync(new CreateDeploymentControlWorkCommand(Guid.NewGuid(),v.Id,v.Revision,"Cancel","cancel pending")); await Prepare(f,cancel.WorkId!.Value);
        Assert.Equal("Canceled",(await f.SendAsync(new GetInstanceTaskQuery(t.Id))).State);
        var items=await f.SendAsync(new ListTaskControlItemsQuery(cancel.WorkId.Value)); Assert.Equal("Applied",Assert.Single(items.Items).Outcome);
        user=await f.Instances.Site.UserAsync(user.Id);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),user.Id,user.Revision,user.Permissions.Where(p=>p.Operation!="deployment.control").ToArray(),"夹具撤销当前控制授权"));
        var outbox=await OutboxFixture.MessagesAsync(f.Database);
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(control)); Assert.Equal(outbox,await OutboxFixture.MessagesAsync(f.Database));
    }
    [Fact]
    public async Task LostCommitIsVerifiedOnceAndOldLeaseCannotAdvance()
    {
        await using var f=await TaskFixture.CreateAsync(); var fault=new LostCommit(); var command=new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Filter",new(f.SoftwareId)));
        var before=await f.Count("tsk.target_selections"); var result=await f.SendAsync(command,interceptor:fault); Assert.Equal(1,fault.Faults);
        Assert.Equal(before+1,await f.Count("tsk.target_selections")); Assert.Equal(result,await f.SendAsync(command)); await Accept(f,result.WorkId!.Value);
        var old=(await f.SendAsync(new ClaimTaskWorkCommand(result.WorkId.Value,Guid.NewGuid()),work:result.WorkId)).Value!;
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(61)); var fresh=(await f.SendAsync(new ClaimTaskWorkCommand(result.WorkId.Value,Guid.NewGuid()),work:result.WorkId)).Value!;
        Assert.True(fresh.Generation>old.Generation);
        await Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new MaterializeTaskTargetsCommand(old),work:result.WorkId));
        Assert.Equal("Sealed",(await f.SendAsync(new MaterializeTaskTargetsCommand(fresh),work:result.WorkId)).Value.State);
    }
    [Fact]
    public async Task ResumeBeforeAcceptanceCommitsNewDispatchAndOutboxOnceAndRejectsOldGeneration()
    {
        await using var f=await TaskFixture.CreateAsync(); var release=await Formal(f); var instance=await Enroll(f);
        var created=await Deployment(f,release.Id,[instance.Id]); var workId=created.WorkId!.Value;
        var original=await Authority(f,workId);
        var paused=(await f.SendAsync(new ControlDeploymentCommand(Guid.NewGuid(),created.Value.Id,created.Value.Revision,"Pause","hold before acceptance"))).Value;
        var resume=new ControlDeploymentCommand(Guid.NewGuid(),paused.Id,paused.Revision,"Resume","resume original work");
        var dispatches=await f.Count("tsk.dispatches"); var outbox=await OutboxFixture.MessagesAsync(f.Database); var audits=await f.Count("aud.events"); var results=await f.Count("tsk.operation_results");
        await Assert.ThrowsAsync<PersistenceException>(()=>f.SendAsync(resume,interceptor:new SaveFailure()));
        Assert.Equal(original,await Authority(f,workId)); Assert.Equal(dispatches,await f.Count("tsk.dispatches")); Assert.Equal(outbox,await OutboxFixture.MessagesAsync(f.Database));
        Assert.Equal(audits,await f.Count("aud.events")); Assert.Equal(results,await f.Count("tsk.operation_results"));
        var fault=new LostCommit(); var restored=await f.SendAsync(resume,interceptor:fault); Assert.Equal(1,fault.Faults);
        var current=await Authority(f,workId); Assert.Equal(original.DispatchSequence+1,current.DispatchSequence); Assert.NotEqual(original.DispatchEventId,current.DispatchEventId);
        Assert.Equal(dispatches+1,await f.Count("tsk.dispatches")); Assert.Equal(outbox+1,await OutboxFixture.MessagesAsync(f.Database));
        Assert.Equal(restored,await f.SendAsync(resume)); Assert.Equal(outbox+1,await OutboxFixture.MessagesAsync(f.Database));
        await using(var provider=f.Provider()) await using(var scope=provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(),async ct=>
            { await scope.ServiceProvider.GetRequiredService<ITaskWorkflow>().AcceptAsync(workId,original.DispatchSequence,original.DispatchEventId,ct); return true; },default);
        Assert.False((await Authority(f,workId)).Accepted); Assert.Null((await f.SendAsync(new ClaimTaskWorkCommand(workId,Guid.NewGuid()),work:workId)).Value);
        await Prepare(f,workId); Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,created.Value.Id)))).Items);
    }
    [Fact]
    public async Task AcceptedControlFailureResumesOriginalCursorWithoutRegisteringAnotherMessage()
    {
        await using var f=await TaskFixture.CreateAsync(); var release=await Formal(f); var one=await Enroll(f); var two=await Enroll(f); var three=await Enroll(f);
        var d=await Deployment(f,release.Id,[one.Id,two.Id,three.Id]); await Prepare(f,d.WorkId!.Value);
        var view=await f.SendAsync(new GetDeploymentQuery(d.Value.Id));
        var cancel=await f.SendAsync(new CreateDeploymentControlWorkCommand(Guid.NewGuid(),view.Id,view.Revision,"Cancel","persistent control recovery")); var id=cancel.WorkId!.Value;
        await Accept(f,id); var first=(await f.SendAsync(new ClaimTaskWorkCommand(id,Guid.NewGuid()),work:id)).Value!;
        var partial=(await f.SendAsync(new AdvanceTaskWorkCommand(first),work:id)).Value; Assert.Equal(2,partial.ProcessedItems);
        var failedLease=(await f.SendAsync(new ClaimTaskWorkCommand(id,Guid.NewGuid()),work:id)).Value!;
        await f.SendAsync(new FailTaskWorkCommand(failedLease,"FIXTURE_INTERRUPTED"),work:id);
        var dispatches=await f.Count("tsk.dispatches"); var outbox=await OutboxFixture.MessagesAsync(f.Database); var workCount=await f.Count("tsk.works");
        var paused=await f.SendAsync(new GetDeploymentQuery(view.Id));
        await f.SendAsync(new ControlDeploymentCommand(Guid.NewGuid(),paused.Id,paused.Revision,"Resume","resume original accepted control"));
        Assert.Equal(2,(await f.SendAsync(new GetTaskWorkQuery(id))).ProcessedItems); Assert.Equal(dispatches,await f.Count("tsk.dispatches")); Assert.Equal(outbox,await OutboxFixture.MessagesAsync(f.Database));
        await Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new AdvanceTaskWorkCommand(failedLease),work:id));
        await Prepare(f,id); Assert.Equal("Completed",(await f.SendAsync(new GetTaskWorkQuery(id))).State);
        Assert.Equal(3,(await f.SendAsync(new ListTaskControlItemsQuery(id))).Items.Count); Assert.Equal(workCount,await f.Count("tsk.works"));
    }
    private static async Task<TaskWorkAuthority> Authority(TaskFixture f,Guid id)
    { await using var provider=f.Provider(); await using var scope=provider.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ITaskWorkflow>().AuthorityAsync(id,false,default); }
    private sealed class SaveFailure : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e,InterceptionResult<int> result,CancellationToken ct=default) => throw new IOException("controlled resume save failure"); }
    private static StateReport NotInstalled(long seq) => InstanceFixture.Report(sequence:seq,version:null) with { InstallationState="NotInstalled" };
    private sealed class LostCommit : DbTransactionInterceptor
    {
        internal int Faults;
        public override Task TransactionCommittedAsync(DbTransaction tx,TransactionEndEventData e,CancellationToken ct=default)
        { if(Interlocked.CompareExchange(ref Faults,1,0)==0) throw new IOException("task fixture lost commit confirmation"); return Task.CompletedTask; }
    }
}
