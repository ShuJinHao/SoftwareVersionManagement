using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;
using Xunit;
using static Svm.FrameworkTests.InstanceAccessTests;

namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class InstanceReportTests
{
    [Fact]
    public async Task DuplicateCurrentReportDoesNotRefreshTimeAndFiveMinuteBoundaryUsesServerClock()
    {
        await using var f=await InstanceFixture.CreateAsync(); var e=await f.EnrollAsync(); var id=e.Registration.InstanceId;
        var streamKey=Guid.NewGuid(); var stream=new OpenReportStreamCommand(streamKey,0); await f.SendAsync(stream,e.Proof,id); Assert.Equal(1,(await f.SendAsync(stream,e.Proof,id)).Value.StreamEpoch);
        var r=InstanceFixture.Report() with { ReportedAt=f.Clock.GetUtcNow().AddYears(20) }; var first=(await f.SendAsync(new SubmitStatusReportCommand(r),e.Proof,id)).Value;
        var before=await f.CountAsync("ins.operation_results"); f.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(first,(await f.SendAsync(new SubmitStatusReportCommand(r),e.Proof,id)).Value); var exact=await f.SendAsync(new GetInstanceQuery(id)); Assert.Equal("Fresh",exact.Freshness); Assert.Equal(300,exact.UnreportedSeconds);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1)); var stale=await f.SendAsync(new GetInstanceQuery(id)); Assert.Equal("Unknown",stale.Freshness); Assert.Equal(first.LastAcceptedAt,stale.LastAcceptedAt); Assert.Equal("1.2.3",stale.LastSnapshot!.InstalledVersion);
        Assert.Equal(before,await f.CountAsync("ins.operation_results")); Assert.Equal(1,await f.CountAsync("ins.installation_evidence"));
        var newer=(await f.SendAsync(new SubmitStatusReportCommand(r with { ReportSeq=2 }),e.Proof,id)).Value; Assert.True(newer.Applied); Assert.NotEqual(first.LastAcceptedAt,newer.LastAcceptedAt); Assert.Equal("Fresh",(await f.SendAsync(new GetInstanceQuery(id))).Freshness);
        Assert.Equal(1,await f.CountAsync("ins.installation_evidence"));
    }
    [Fact]
    public async Task ConflictingOrStaleSequencesAndOldStreamsCannotReplaceTheSnapshot()
    {
        await using var f=await InstanceFixture.CreateAsync(); var e=await f.EnrollAsync(); var id=e.Registration.InstanceId;
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),0),e.Proof,id); var r=InstanceFixture.Report(1,2); await f.SendAsync(new SubmitStatusReportCommand(r),e.Proof,id);
        await Rejected(RequestFailure.ReportConflict,()=>f.SendAsync(new SubmitStatusReportCommand(r with { RunningState="Stopped" }),e.Proof,id));
        Assert.False((await f.SendAsync(new SubmitStatusReportCommand(r with { ReportSeq=1,InstalledVersion="0.1.0" }),e.Proof,id)).Value.Applied);
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),1),e.Proof,id);
        Assert.False((await f.SendAsync(new SubmitStatusReportCommand(r with { ReportSeq=3 }),e.Proof,id)).Value.Applied);
        var current=(await f.SendAsync(new SubmitStatusReportCommand(r with { StreamEpoch=2,ReportSeq=1,InstalledVersion="2.0.0" }),e.Proof,id)).Value; Assert.True(current.Applied);
        await Rejected(RequestFailure.RevisionConflict,()=>f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),1),e.Proof,id));
        Assert.Equal("2.0.0",(await f.SendAsync(new GetInstanceQuery(id))).LastSnapshot!.InstalledVersion); Assert.Equal(2,(await f.SendAsync(new GetInstanceHistoryQuery(id))).Items.Count);
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(new SubmitStatusReportCommand(r with { StreamEpoch=2,ReportSeq=2,InstalledReleaseId=Guid.NewGuid() }),e.Proof,id));
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task ReportAndInstallationEvidenceRollbackTogether(string stage)
    {
        await using var f=await InstanceFixture.CreateAsync(); var e=await f.EnrollAsync(); var id=e.Registration.InstanceId;
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),0),e.Proof,id); var audit=await f.CountAsync("aud.events");
        using var ct=new CancellationTokenSource(); var interceptor=stage=="save"?(Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor)new SaveFailure():stage=="cancel"?new SaveCancellation(ct):null;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report()),e.Proof,id,interceptor:interceptor,auditFailure:stage=="audit",token:ct.Token));
        Assert.Equal(0,await f.CountAsync("ins.installation_evidence")); Assert.Equal(audit,await f.CountAsync("aud.events")); Assert.Equal("NeverReported",(await f.SendAsync(new GetInstanceQuery(id))).Freshness);
        Assert.Equal(0,(await f.SendAsync(new GetClientContextQuery(),e.Proof,id)).LastReportSeq);
    }
    [Fact]
    public async Task LostReportAndStreamCommitConfirmationsOnlyVerifyCommittedFacts()
    {
        await using var f=await InstanceFixture.CreateAsync(); var e=await f.EnrollAsync(); var id=e.Registration.InstanceId;
        var stream=new OpenReportStreamCommand(Guid.NewGuid(),0); var lostStream=new LostConfirmation(); Assert.Equal(1,(await f.SendAsync(stream,e.Proof,id,interceptor:lostStream)).Value.StreamEpoch); Assert.Equal(1,lostStream.Commits);
        var lostReport=new LostConfirmation(); var r=new SubmitStatusReportCommand(InstanceFixture.Report()); Assert.True((await f.SendAsync(r,e.Proof,id,interceptor:lostReport)).Value.Applied); Assert.Equal(1,lostReport.Commits);
        Assert.True((await f.SendAsync(r,e.Proof,id)).Value.Applied); Assert.Equal(1,await f.CountAsync("ins.installation_evidence")); Assert.Equal(1,await f.CountAsync("ins.report_stream_receipts"));
    }
    [Fact]
    public async Task ConcurrentReportsHaveOneFactAndSuspensionAffectsOnlyApiAccess()
    {
        await using var f=await InstanceFixture.CreateAsync(); var e=await f.EnrollAsync(); var id=e.Registration.InstanceId;
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),0),e.Proof,id); var r=new SubmitStatusReportCommand(InstanceFixture.Report()); var results=await Task.WhenAll(f.SendAsync(r,e.Proof,id),f.SendAsync(r,e.Proof,id)); Assert.Equal(results[0].Value,results[1].Value); Assert.Equal(1,await f.CountAsync("ins.installation_evidence"));
        await f.SendAsync(new UpdateInstanceLifecycleCommand(Guid.NewGuid(),id,1,"Suspended","夹具暂停接入")); await Rejected(RequestFailure.InstanceSuspended,()=>f.SendAsync(new GetClientContextQuery(),e.Proof,id));
        var view=await f.SendAsync(new GetInstanceQuery(id)); Assert.Equal("Running",view.LastSnapshot!.RunningState); Assert.Equal("Suspended",view.Lifecycle);
        await f.SendAsync(new UpdateInstanceLifecycleCommand(Guid.NewGuid(),id,2,"Active","恢复接入")); Assert.Equal(1,(await f.SendAsync(new GetClientContextQuery(),e.Proof,id)).LastReportSeq);
    }
    [Fact]
    public async Task MultiInstanceInventoryFiltersBeforePagingAndHistoryDoesNotRepeatHeartbeats()
    {
        await using var f=await InstanceFixture.CreateAsync(); var a=await f.EnrollAsync(); var b=await f.EnrollAsync();
        var first=await f.SendAsync(new GetDeviceInventoryQuery(f.Device.Id,new(new(),1))); Assert.Single(first.Items); Assert.NotNull(first.Next); var next=await f.SendAsync(new GetDeviceInventoryQuery(f.Device.Id,new(new(),1,first.Next))); Assert.Single(next.Items); Assert.Null(next.Next); Assert.NotEqual(first.Items[0].Instance!.Id,next.Items[0].Instance!.Id);
        var proof=await f.Site.PersonAsync([new(null,"asset.read"),new(f.SoftwareId,"software.read")]); Assert.Empty((await f.SendAsync(new GetDeviceInventoryQuery(f.Device.Id,new(new(),50)),human:proof)).Items);
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(new GetInstanceQuery(a.Registration.InstanceId),human:proof));
        var page=await f.SendAsync(new ListInstancesQuery(new(new(f.SoftwareId),1))); Assert.Single(page.Items); Assert.NotNull(page.Next); var page2=await f.SendAsync(new ListInstancesQuery(new(new(f.SoftwareId),1,page.Next))); Assert.Single(page2.Items); Assert.Null(page2.Next);
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),0),a.Proof,a.Registration.InstanceId); var report=InstanceFixture.Report(); await f.SendAsync(new SubmitStatusReportCommand(report),a.Proof,a.Registration.InstanceId); await f.SendAsync(new SubmitStatusReportCommand(report with { ReportSeq=2 }),a.Proof,a.Registration.InstanceId);
        Assert.Single((await f.SendAsync(new ListInstancesQuery(new(new(f.SoftwareId,ReportedIp:"192.0.2.10",Freshness:"Fresh"),50)))).Items); Assert.Single((await f.SendAsync(new GetInstanceHistoryQuery(a.Registration.InstanceId))).Items);
        Assert.Equal("NeverReported",(await f.SendAsync(new GetInstanceQuery(b.Registration.InstanceId))).Freshness);
    }
    [Theory][InlineData("NotInstalled","1.0.0","None",false)][InlineData("Installed",null,"None",false)][InlineData("Installed","1.0.0","Present",false)][InlineData("Unknown",null,"Unknown",true)]
    public void SnapshotInputHasExplicitInstallationAndDatabaseState(string state,string? version,string db,bool valid)
    { Assert.Equal(valid,InstanceValidation.Report(InstanceFixture.Report() with { InstallationState=state,InstalledVersion=version,DatabaseState=new(db,[]) })); }
}
