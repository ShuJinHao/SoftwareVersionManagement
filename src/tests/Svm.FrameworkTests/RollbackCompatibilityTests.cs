using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.Core.Releases;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Tasks;
using Xunit;
using static Svm.FrameworkTests.DeploymentWorkflowTests;
using static Svm.FrameworkTests.InstanceAccessTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class RollbackCompatibilityTests
{
    [Fact]
    public void DatabaseCompatibilityRequiresTheCompleteExplicitSetAndExactSchemas()
    {
        var required = new Dictionary<string, IReadOnlyList<string>> { ["recipe"] = ["v1","v2"], ["history"] = ["h1"] };
        Assert.True(DatabaseCompatibility.Accepts("None", new Dictionary<string,IReadOnlyList<string>>(), "None", []));
        Assert.False(DatabaseCompatibility.Accepts("None", new Dictionary<string,IReadOnlyList<string>>(), "Unknown", []));
        Assert.False(DatabaseCompatibility.Accepts("None", new Dictionary<string,IReadOnlyList<string>>(), null, null));
        Assert.True(DatabaseCompatibility.Accepts("Present", required, "Present", [new("history","h1"), new("recipe","v2")]));
        Assert.False(DatabaseCompatibility.Accepts("Present", required, "Present", [new("recipe","v2")]));
        Assert.False(DatabaseCompatibility.Accepts("Present", required, "Present", [new("recipe","v3"),new("history","h1")]));
        Assert.False(DatabaseCompatibility.Accepts("Present", required, "Present", [new("recipe","v2"),new("history","h1"),new("extra","e1")]));
        Assert.False(DatabaseCompatibility.Accepts("Present", required, "Present", [new("recipe","v2"),new("recipe","v2")]));
    }

    [Fact]
    public async Task DeclarationsAppendConcurrentRevisionsKeepReplayAndCheckCurrentProductAuthority()
    {
        await using var f = await TaskFixture.CreateAsync(); var target = await Formal(f); var source = await Formal(f);
        var command = Declare(target, source); var original = (await f.SendAsync(command)).Value;
        var two = await Task.WhenAll(f.SendAsync(command with { Key = Guid.NewGuid() }), f.SendAsync(command with { Key = Guid.NewGuid() }));
        Assert.Equal(new long[] {2,3}, two.Select(x=>x.Value.DeclarationRevision).Order());
        var history = await f.SendAsync(new GetCompatibilityDeclarationsQuery(target.Id,1)); Assert.Equal(3,Assert.Single(history).DeclarationRevision);
        Assert.Equal(2,Assert.Single(await f.SendAsync(new GetCompatibilityDeclarationsQuery(target.Id,1,history[0].Id))).DeclarationRevision);
        Assert.Equal(original.Id,(await f.SendAsync(command)).Value.Id); Assert.Equal(3,await f.Count("rel.compatibility_declarations"));
        await Rejected(RequestFailure.IdempotencyConflict,()=>f.SendAsync(command with { Input=command.Input with { Reason="changed" } }));
        await Rejected(RequestFailure.ValidationFailed,()=>f.SendAsync(command with { Key=Guid.NewGuid(),Input=command.Input with { SourceReleaseIds=[source.Id,source.Id] } }));
        await Rejected(RequestFailure.ValidationFailed,()=>f.SendAsync(command with { Key=Guid.NewGuid(),Input=command.Input with { DatabaseRequirements=[new("extra",["v1"])] } }));
        await Assert.ThrowsAsync<PostgresException>(()=>PersistenceDatabase.ExecuteAsync(f.Database.WriterConnection,"UPDATE rel.compatibility_declarations SET \"Statement\"='overwrite'"));
        await ChangePermission(f,"release.upload",false);
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(command));
        Assert.Equal(original.Id,(await f.SendAsync(new GetCompatibilityDeclarationsQuery(target.Id))).Last().Id);
    }

    [Fact]
    public async Task PartialRollbackAdmitsOnlyMatchingInstancesAndRetainsInstalledFactsUntilReported()
    {
        await using var f = await TaskFixture.CreateAsync(); await ChangePermission(f,"deployment.rollback",true);
        var target=await Formal(f); var source=await Formal(f);
        var declaration=(await f.SendAsync(Declare(target,source) with {Input=Input(source,"Present")})).Value;
        var good=await Installed(f,source,new("Present",[new("recipe","v1")]));
        var bad=await Installed(f,source,new("Present",[new("recipe","v2")]));
        var unknown=await Installed(f,source,new("Unknown",[])); var notInstalled=await Enroll(f); var untouched=await Installed(f,source,new("None",[]));
        var command=await RollbackCommand(f,target.Id,[good.Id,bad.Id,unknown.Id,notInstalled.Id]);
        var created=await f.SendAsync(command); Assert.Equal("Rollback",created.Value.Kind); await Prepare(f,created.WorkId!.Value); await Tick(f,created.Value.Id);
        var admission=(await f.SendAsync(new ListDeploymentTargetsQuery(created.Value.Id))).Items;
        Assert.Equal(4,admission.Count); Assert.Equal(good.Id,Assert.Single(admission,x=>x.Decision=="Accepted").InstanceId);
        Assert.Equal(2,admission.Count(x=>x.ReasonCode=="DATABASE_INCOMPATIBLE")); Assert.Single(admission,x=>x.ReasonCode=="CURRENT_VERSION_UNKNOWN");
        var task=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,created.Value.Id)))).Items);
        Assert.Equal("Rollback",task.Kind); Assert.Equal(declaration.Id,task.AdmissionCompatibility!.DeclarationId);
        var attempt=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),task.Id),access:good.Proof,instance:good.Id)).Value;
        var permit=(await f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),attempt,new(Report(source,3,new("Present",[new("recipe","v1")])),new(PackageFixture.Input.Sha256,true,1))),access:good.Proof,instance:good.Id)).Value;
        Assert.Equal("Rollback",permit.Kind); Assert.Equal(source.Id,permit.Compatibility!.SourceReleaseId);
        await f.SendAsync(new SubmitTaskReceiptCommand(attempt,new(Guid.NewGuid(),1,"Terminal",Result:"Succeeded")),access:good.Proof,instance:good.Id);
        Assert.Equal(source.Id,(await f.SendAsync(new GetInstanceQuery(good.Id))).LastSnapshot!.InstalledReleaseId);
        Assert.Null((await f.SendAsync(new GetInstanceQuery(untouched.Id))).LatestTaskId);
        await f.SendAsync(new SubmitStatusReportCommand(Report(target,4,new("Present",[new("recipe","v1")]))),access:good.Proof,instance:good.Id);
        Assert.Equal(target.Id,(await f.SendAsync(new GetInstanceQuery(good.Id))).LastSnapshot!.InstalledReleaseId);
        Assert.Equal(source.Id,(await f.SendAsync(new GetInstanceQuery(bad.Id))).LastSnapshot!.InstalledReleaseId);
    }

    [Fact]
    public async Task StartRechecksLatestDeclarationAndDatabaseWithoutSavingDeniedReportsOrReexecutingUnknownCommit()
    {
        await using var f=await TaskFixture.CreateAsync(); await ChangePermission(f,"deployment.rollback",true);
        var target=await Formal(f); var source=await Formal(f); await f.SendAsync(Declare(target,source));
        var i=await Installed(f,source,new("None",[])); var d=await f.SendAsync(await RollbackCommand(f,target.Id,[i.Id])); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var task=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items);
        var attempt=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),task.Id),access:i.Proof,instance:i.Id)).Value;
        await f.SendAsync(Declare(target,source) with {Input=Input(source,"Present")});
        var start=new StartInstanceTaskCommand(Guid.NewGuid(),attempt,new(Report(source,3,new("None",[])),new(PackageFixture.Input.Sha256,true,1)));
        await Rejected(RequestFailure.CompatibilityRevisionConflict,()=>f.SendAsync(start,access:i.Proof,instance:i.Id));
        await Rejected(RequestFailure.DatabaseIncompatible,()=>f.SendAsync(start with {Input=start.Input with {Preflight=start.Input.Preflight with {CompatibilityDeclarationRevision=2}}},access:i.Proof,instance:i.Id));
        Assert.Equal(2,(await f.SendAsync(new GetInstanceQuery(i.Id))).LastSnapshot!.ReportSeq);
        Assert.Equal(2,(await f.SendAsync(new GetClientTaskQuery(task.Id),access:i.Proof,instance:i.Id)).CompatibilityDeclarationRevision);
        Assert.Equal(2,(await f.SendAsync(new GetClientReleaseQuery(target.Id),access:i.Proof,instance:i.Id)).CompatibilityDeclarationRevision);
        var revision=(await f.SendAsync(Declare(target,source))).Value;
        start=start with {Input=start.Input with {Preflight=start.Input.Preflight with {CompatibilityDeclarationRevision=revision.DeclarationRevision}}};
        var audits=await f.Count("aud.events"); var results=await f.Count("tsk.operation_results");
        await Assert.ThrowsAsync<PersistenceException>(()=>f.SendAsync(start,access:i.Proof,instance:i.Id,auditFailure:true));
        Assert.Equal(audits,await f.Count("aud.events")); Assert.Equal(results,await f.Count("tsk.operation_results")); Assert.Null((await f.SendAsync(new GetInstanceTaskQuery(task.Id))).StartAuthorizedAt);
        var loss=new LostConfirmation(); var permit=await f.SendAsync(start,access:i.Proof,instance:i.Id,interceptor:loss);
        Assert.Equal(1,loss.Commits); Assert.Equal(revision.Id,permit.Value.Compatibility!.DeclarationId);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(permit.Value),System.Text.Json.JsonSerializer.Serialize((await f.SendAsync(start,access:i.Proof,instance:i.Id)).Value));
        Assert.Equal(audits+1,await f.Count("aud.events")); Assert.Equal(results+1,await f.Count("tsk.operation_results"));
        Assert.Equal(3,(await f.SendAsync(new GetInstanceQuery(i.Id))).LastSnapshot!.ReportSeq);
    }

    [Fact]
    public async Task RollbackNeedsAdditionalCurrentPermissionAndRevocationKeepsAuthorizedResultsTracked()
    {
        await using var f=await TaskFixture.CreateAsync(); var target=await Formal(f); var source=await Formal(f); await f.SendAsync(Declare(target,source));
        var one=await Installed(f,source,new("None",[])); var two=await Installed(f,source,new("None",[]));
        var command=await RollbackCommand(f,target.Id,[one.Id,two.Id]);
        await Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(command)); await ChangePermission(f,"deployment.rollback",true);
        var d=await f.SendAsync(command); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var tasks=(await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items; Assert.Equal(2,tasks.Count);
        var task=tasks.Single(x=>x.InstanceId==one.Id); var other=tasks.Single(x=>x.InstanceId==two.Id);
        var attempt=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),task.Id),access:one.Proof,instance:one.Id)).Value;
        var otherAttempt=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),other.Id),access:two.Proof,instance:two.Id)).Value;
        await f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),attempt,new(Report(source,3,new("None",[])),new(PackageFixture.Input.Sha256,true,1))),access:one.Proof,instance:one.Id);
        await ChangePermission(f,"deployment.rollback",false);
        await Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(command));
        await Rejected(RequestFailure.PermissionDenied,()=>f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),otherAttempt,new(Report(source,3,new("None",[])),new(PackageFixture.Input.Sha256,true,1))),access:two.Proof,instance:two.Id));
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(31)); await Tick(f,d.Value.Id);
        var after=await f.SendAsync(new GetDeploymentQuery(d.Value.Id)); Assert.Equal("Paused",after.State); Assert.Contains(after.PauseReasons,x=>x.Code=="AuthorizationChanged"); Assert.Contains(after.PauseReasons,x=>x.Code=="ReceiptTimeout");
        await f.SendAsync(new SubmitTaskReceiptCommand(attempt,new(Guid.NewGuid(),1,"Terminal",Result:"Succeeded")),access:one.Proof,instance:one.Id);
        Assert.Equal("Succeeded",(await f.SendAsync(new GetInstanceTaskQuery(task.Id))).State);
        Assert.Equal("Preparing",(await f.SendAsync(new GetInstanceTaskQuery(other.Id))).State);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("save")]
    [InlineData("cancel")]
    public async Task DeclarationBusinessAuditAndIdempotencyRollBackTogether(string stage)
    {
        await using var f=await TaskFixture.CreateAsync(); var target=await Formal(f); var source=await Formal(f);
        var audits=await f.Count("aud.events"); var results=await f.Count("rel.operation_results");
        using var cancel=new CancellationTokenSource(); IInterceptor? fault=stage=="save" ? new SaveFailure() : stage=="cancel" ? new SaveCancellation(cancel) : null;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.SendAsync(Declare(target,source),auditFailure:stage=="audit",interceptor:fault,token:cancel.Token));
        Assert.Equal(0,await f.Count("rel.compatibility_declarations")); Assert.Equal(audits,await f.Count("aud.events")); Assert.Equal(results,await f.Count("rel.operation_results"));
    }

    private static RecordCompatibilityCommand Declare(ReleaseView target,ReleaseView source) => new(Guid.NewGuid(),target.Id,Input(source));
    private static CompatibilityInput Input(ReleaseView source,string mode="None") => new([source.Id],mode,mode=="None" ? [] : [new("recipe",["v1"])],"虚构夹具验证完整数据库条件","夹具兼容依据");
    private static StateReport Report(ReleaseView release,long seq,DatabaseState databases) => InstanceFixture.Report(sequence:seq,version:release.Version) with {InstalledReleaseId=release.Id,DatabaseState=databases};
    private static async Task<(Guid Id,AccessProof Proof)> Installed(TaskFixture f,ReleaseView release,DatabaseState databases)
    { var i=await Enroll(f); await f.SendAsync(new SubmitStatusReportCommand(Report(release,2,databases)),access:i.Proof,instance:i.Id); return i; }
    internal static async Task ChangePermission(TaskFixture f,string operation,bool grant)
    { var u=await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId); var remaining=u.Permissions.Where(p=>p.Operation!=operation).ToList(); if(grant) remaining.Add(new(f.SoftwareId,operation)); await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),u.Id,u.Revision,remaining,"夹具显式修改单项权限")); }
    private static async Task<CreateDeploymentCommand> RollbackCommand(TaskFixture f,Guid target,Guid[] ids)
    {
        var s=(await f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Explicit")))).Value;
        var chunks=ids.Chunk(TaskFixture.TaskOptions.SelectionChunkSize!.Value).ToArray();
        for(var n=0;n<chunks.Length;n++) s=(await f.SendAsync(new PutTargetChunkCommand(s.Id,n,chunks[n]))).Value;
        await f.SendAsync(new SealTargetSelectionCommand(Guid.NewGuid(),s.Id,s.Revision,chunks.Length,ids.Length));
        var now=f.Instances.Clock.GetUtcNow(); return new(Guid.NewGuid(),new(f.SoftwareId,s.Id,"Rollback",target,new(now.AddSeconds(-1),now.AddHours(1)),"夹具选择部分机台回退"));
    }
}
