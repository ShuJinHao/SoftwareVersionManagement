using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Tasks;
using Xunit;
using static Svm.FrameworkTests.DeploymentWorkflowTests;

namespace Svm.FrameworkTests;
[Trait("Category","Business")]
public sealed class HardwareTaskTests
{
    [Fact]
    public async Task NewAdmissionRequiresHardwareAndStartRechecksFrozenPositionWithoutSavingReport()
    {
        await using var f=await TaskFixture.CreateAsync(); var release=await Formal(f); var unbound=await Enroll(f); var bound=await Enroll(f);
        await Unbind(f,unbound.Id);
        var d=await Deployment(f,release.Id,[unbound.Id,bound.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var results=(await f.SendAsync(new ListDeploymentTargetsQuery(d.Value.Id))).Items;
        Assert.Equal("HARDWARE_BINDING_REQUIRED",Assert.Single(results,x=>x.InstanceId==unbound.Id).ReasonCode);
        var task=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items);
        Assert.NotNull(task.HardwareBindingId); Assert.Equal(bound.Id,task.InstanceId);
        var claim=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),task.Id),access:bound.Proof,instance:bound.Id)).Value;
        await Unbind(f,bound.Id);
        var before=await f.SendAsync(new GetInstanceQuery(bound.Id));
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState,()=>f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),claim,new(InstanceFixture.Report(sequence:2),new(PackageFixture.Input.Sha256,true))),access:bound.Proof,instance:bound.Id));
        var after=await f.SendAsync(new GetInstanceQuery(bound.Id)); Assert.Equal(before.LastAcceptedAt,after.LastAcceptedAt); Assert.Equal(1,after.LastSnapshot!.ReportSeq);
    }
    [Fact]
    public async Task LegacyTaskWithoutHardwareStillAcceptsPermitReceiptAndTimeoutTracking()
    {
        await using var f=await TaskFixture.CreateAsync(); var release=await Formal(f); var i=await Enroll(f);
        var d=await Deployment(f,release.Id,[i.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var task=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items);
        await PersistenceDatabase.ExecuteAsync(f.Database.MigrationConnection,$"UPDATE tsk.tasks SET \"HardwareBindingId\"=NULL WHERE \"Id\"='{task.Id}'");
        await Unbind(f,i.Id);
        var claim=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),task.Id),access:i.Proof,instance:i.Id)).Value;
        var report=InstanceFixture.Report(sequence:2,version:null) with { InstallationState="NotInstalled" };
        await f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),claim,new(report,new(PackageFixture.Input.Sha256,true))),access:i.Proof,instance:i.Id);
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(31)); await Tick(f,d.Value.Id);
        Assert.Equal("Paused",(await f.SendAsync(new GetDeploymentQuery(d.Value.Id))).State);
        var received=await f.SendAsync(new SubmitTaskReceiptCommand(claim,new(Guid.NewGuid(),3,"Terminal",Result:"Succeeded")),access:i.Proof,instance:i.Id);
        Assert.True(received.Value.Applied);
    }
    private static Task Unbind(TaskFixture f,Guid id)=>PersistenceDatabase.ExecuteAsync(f.Database.MigrationConnection,$"UPDATE ins.instances SET \"HardwareBindingId\"=NULL WHERE \"Id\"='{id}'; UPDATE ins.hardware_software_bindings SET \"InstanceId\"=NULL WHERE \"InstanceId\"='{id}'");
}
