using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Packages;
using Xunit;
using static Svm.FrameworkTests.InstanceAccessTests;
namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class PackageCleanupTests
{
    internal static async Task Grant(PackageFixture f)
    {
        var u = await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),u.Id,u.Revision,
            u.Permissions.Append(new(f.SoftwareId,"package.clean")).ToArray(),"夹具显式授予清理"));
    }
    internal static async Task<ReleaseUploadResult> Ready(PackageFixture f)
    {
        var r=(await f.SendAsync(f.Create())).Value; var lease=await f.Lease(r);
        await f.SendAsync(new CompletePackageWorkCommand(lease,f.Healthy),work:r.UploadId); return r;
    }
    internal static async Task<PackageCleanupStatus> Disable(PackageFixture f, ReleaseUploadResult r)
    {
        var release=await f.SendAsync(new GetReleaseQuery(r.Release.Id));
        await f.SendAsync(new DisableReleaseCommand(Guid.NewGuid(),release.Id,release.Revision,"夹具停用"));
        return await f.SendAsync(new GetPackageCleanupStatusQuery(r.Release.PackageId));
    }
    internal static async Task<PackageLease> Accept(PackageFixture f, Guid id)
    {
        await using(var p=f.Provider())
        await using(var s=p.CreateAsyncScope())
        {
            var packages=s.ServiceProvider.GetRequiredService<IPackages>(); var w=await packages.AuthorityAsync(id,false,default);
            await s.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(),async ct=>{await packages.AcceptAsync(id,w.DispatchSequence,w.DispatchEventId,ct);return true;},default);
        }
        return (await f.SendAsync(new ClaimPackageWorkCommand(id,"node-a",Guid.NewGuid()),work:id)).Value!;
    }
    [Fact] public async Task CleanupRequiresDisabledReleaseAndExplicitProductPermission()
    {
        await using var f=await PackageFixture.CreateAsync(); var r=await Ready(f);
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(new GetPackageCleanupStatusQuery(r.Release.PackageId)));
        await Grant(f); var s=await f.SendAsync(new GetPackageCleanupStatusQuery(r.Release.PackageId));
        Assert.Contains("RELEASE_NOT_DISABLED",s.BlockingReasons);
        await Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new RequestPackageCleanupCommand(Guid.NewGuid(),s.PackageId,s.Revision,"还未停用")));
        Assert.Equal(0,await f.Count("pkg.cleanup_intents")); Assert.True((await f.SendAsync(new GetPackageQuery(s.PackageId))).DownloadAvailable);
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task CleanupFailureRollsBackIntentOutboxAuditAndPackageTogether(string stage)
    {
        await using var f=await PackageFixture.CreateAsync(); await Grant(f); var r=await Ready(f); var s=await Disable(f,r);
        var before=await f.Count("aud.events"); var messages=await OutboxFixture.MessagesAsync(f.Database); var operations=await f.Count("pkg.operation_results");
        using var cancel=new CancellationTokenSource();
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? fault=stage=="save"?new SaveFailure():stage=="cancel"?new SaveCancellation(cancel):null;
        var error=await Assert.ThrowsAnyAsync<Exception>(()=>f.SendAsync(new RequestPackageCleanupCommand(Guid.NewGuid(),s.PackageId,s.Revision,"失败整体回滚"),
            interceptor:fault,auditFailure:stage=="audit",token:cancel.Token));
        if(stage=="audit") Assert.Equal(PersistenceFailure.DependencyUnavailable,Assert.IsType<PersistenceException>(error).Failure);
        else if(stage=="save") Assert.Equal("fixture save failure",Assert.IsType<InvalidOperationException>(error).Message);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0,await f.Count("pkg.cleanup_intents")); Assert.Equal(messages,await OutboxFixture.MessagesAsync(f.Database));
        Assert.Equal(before,await f.Count("aud.events")); Assert.Equal(operations,await f.Count("pkg.operation_results"));
        var after=await f.SendAsync(new GetPackageCleanupStatusQuery(s.PackageId)); Assert.Equal("Ready",after.State); Assert.Equal(s.Revision,after.Revision);
    }
    [Fact] public async Task UnknownCleanupRegistrationReplaysBeforeRevisionButRechecksCurrentAuthorization()
    {
        await using var f=await PackageFixture.CreateAsync(); await Grant(f); var r=await Ready(f); var s=await Disable(f,r);
        var command=new RequestPackageCleanupCommand(Guid.NewGuid(),s.PackageId,s.Revision,"核实清理原请求"); var loss=new LostConfirmation();
        var count=await OutboxFixture.MessagesAsync(f.Database); var first=await f.SendAsync(command,interceptor:loss);
        Assert.Equal(1,loss.Commits); Assert.Equal(first.Value.Id,(await f.SendAsync(command)).Value.Id);
        Assert.Equal(count+1,await OutboxFixture.MessagesAsync(f.Database)); Assert.Equal(1,await f.Count("pkg.cleanup_intents"));
        await Rejected(RequestFailure.IdempotencyConflict,()=>f.SendAsync(command with{Reason="不同原因"}));
        await Rejected(RequestFailure.RevisionConflict,()=>f.SendAsync(command with{Key=Guid.NewGuid()}));
        var u=await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),u.Id,u.Revision,u.Permissions.Where(p=>p.Operation!="package.clean").ToArray(),"撤销清理"));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(command));
        await Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(new GetPackageAuthorityQuery(first.Value.Id),work:first.Value.Id));
    }
    [Fact] public async Task PartialDeletionResumesSameIntentAndUnknownFinalCommitPreservesHistory()
    {
        await using var f=await PackageFixture.CreateAsync(); await Grant(f); var r=await Ready(f); var s=await Disable(f,r);
        var request=new RequestPackageCleanupCommand(Guid.NewGuid(),s.PackageId,s.Revision,"删除停用文件");
        var w=(await f.SendAsync(request)).Value; var old=await Accept(f,w.Id);
        await f.SendAsync(new RecordCleanupReplicaCommand(old,"node-a"),work:w.Id);
        await Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new CompletePackageCleanupCommand(old),work:w.Id));
        await f.SendAsync(new FailPackageWorkCommand(old,"CLEANUP_NODE_UNAVAILABLE"),work:w.Id);
        Assert.Equal(1,(await f.SendAsync(new GetPackageWorkQuery(w.Id))).CompletedItems);
        s=await f.SendAsync(new GetPackageCleanupStatusQuery(s.PackageId)); Assert.Equal("Cleaning",s.State);
        Assert.Equal(w.Id,(await f.SendAsync(request with{Key=Guid.NewGuid(),ExpectedRevision=s.Revision})).Value.Id);
        var current=await Accept(f,w.Id);
        await Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new RecordCleanupReplicaCommand(old,"node-b"),work:w.Id));
        await f.SendAsync(new RecordCleanupReplicaCommand(current,"node-a"),work:w.Id);
        await f.SendAsync(new RecordCleanupReplicaCommand(current,"node-b"),work:w.Id);
        var loss=new LostConfirmation();
        var unknown=await Assert.ThrowsAsync<PersistenceException>(()=>f.SendAsync(new CompletePackageCleanupCommand(current),work:w.Id,interceptor:loss));
        Assert.Equal(PersistenceFailure.CommitOutcomeUnknown,unknown.Failure); Assert.Equal(1,loss.Commits);
        Assert.Equal("Cleaned",(await f.SendAsync(new GetPackageCleanupStatusQuery(s.PackageId))).State);
        Assert.Equal("Completed",(await f.SendAsync(request)).Value.State); Assert.Equal(2,(await f.SendAsync(new GetPackageWorkQuery(w.Id))).CompletedItems);
        await f.SendAsync(new CheckPackageReplicasCommand(s.PackageId,f.Healthy),work:s.PackageId);
        Assert.Equal(0,(await f.SendAsync(new GetPackageQuery(s.PackageId))).HealthyReplicaCount);
        Assert.Equal("Disabled",(await f.SendAsync(new GetReleaseQuery(r.Release.Id))).State); Assert.Equal(1,await f.Count("rel.releases"));
        Assert.Equal(1,await f.Count("pkg.cleanup_intents")); Assert.Equal(2,await f.Count("pkg.works"));
    }
    [Fact] public async Task MissingEndNeverExpiresAndStopEvidenceIsScopedAppendOnlyWithoutFakingDownloadSuccess()
    {
        await using var f=await PackageFixture.CreateAsync(); await Grant(f); var r=await Ready(f); var generation=Guid.NewGuid(); var request=Guid.NewGuid();
        await f.SendAsync(new AuthorizeDownloadCommand(r.Release.PackageId,request,"node-a",generation,false,"node-a"),work:r.Release.PackageId,role:"Gateway");
        var otherRequest=Guid.NewGuid(); var otherGeneration=Guid.NewGuid();
        await f.SendAsync(new AuthorizeDownloadCommand(r.Release.PackageId,otherRequest,"node-a",otherGeneration,false,"node-a"),work:r.Release.PackageId,role:"Gateway");
        var s=await Disable(f,r); f.Instances.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Contains("DOWNLOADS_OPEN",(await f.SendAsync(new GetPackageCleanupStatusQuery(s.PackageId))).BlockingReasons);
        await Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new RequestPackageCleanupCommand(Guid.NewGuid(),s.PackageId,s.Revision,"不能猜测结束")));
        var input=new DownloadStopInput("node-a",generation,f.Instances.Clock.GetUtcNow(),true,"夹具实际停止进程证据","夹具运维确认",s.Revision);
        await Rejected(RequestFailure.ValidationFailed,()=>f.SendAsync(new RecordDownloadStopCommand(Guid.NewGuid(),s.PackageId,input with{ProcessStoppedConfirmed=false})));
        var cmd=new RecordDownloadStopCommand(Guid.NewGuid(),s.PackageId,input); var fact=(await f.SendAsync(cmd)).Value;
        Assert.Equal(fact,(await f.SendAsync(cmd)).Value);
        s=await f.SendAsync(new GetPackageCleanupStatusQuery(s.PackageId)); Assert.Equal(otherGeneration,Assert.Single(s.OpenDownloads).WorkerGeneration);
        Assert.Equal(2,await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection,"SELECT count(*) FROM pkg.download_sessions WHERE \"State\"='Open' AND \"BytesSent\" IS NULL"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>PersistenceDatabase.ExecuteAsync(f.Database.WriterConnection,"DELETE FROM pkg.download_stop_evidence"));
        var end=new DownloadEnd(otherRequest,"node-a",otherGeneration,f.Instances.Clock.GetUtcNow(),42,"Closed");
        await f.SendAsync(new RecordDownloadEndCommand(end),work:otherRequest,role:"Collector");
        Assert.Empty((await f.SendAsync(new GetPackageCleanupStatusQuery(s.PackageId))).BlockingReasons);
        await f.SendAsync(new RequestPackageCleanupCommand(Guid.NewGuid(),s.PackageId,s.Revision,"已核实可清理"));
        // A late genuine log is still appended; the manual fact never overwrote the session.
        await f.SendAsync(new RecordDownloadEndCommand(end with{RequestId=request,WorkerGeneration=generation}),work:request,role:"Collector");
        Assert.Equal(2,(await f.SendAsync(new GetDownloadAuditQuery(f.SoftwareId))).Items.Count);
    }
    [Fact] public async Task PendingDeploymentBlocksCleanupEvenWithoutTaskCapabilityInPackageHost()
    {
        await using var f=await TaskFixture.CreateAsync(); await Grant(f.Packages);
        var r=await DeploymentWorkflowTests.Formal(f); var instance=await DeploymentWorkflowTests.Enroll(f);
        await DeploymentWorkflowTests.Deployment(f,r.Id,[instance.Id]);
        var release=await f.SendAsync(new GetReleaseQuery(r.Id));
        await f.SendAsync(new DisableReleaseCommand(Guid.NewGuid(),r.Id,release.Revision,"任务仍引用版本"));
        var s=await f.Packages.SendAsync(new GetPackageCleanupStatusQuery(r.PackageId));
        Assert.Contains("TASKS_UNFINISHED",s.BlockingReasons);
        await Rejected(RequestFailure.InvalidState,()=>f.Packages.SendAsync(new RequestPackageCleanupCommand(Guid.NewGuid(),s.PackageId,s.Revision,"不能越过任务引用")));
    }
}
