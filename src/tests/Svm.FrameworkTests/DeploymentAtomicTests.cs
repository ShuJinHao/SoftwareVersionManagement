using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data.Common;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Instances;
using Xunit;
using static Svm.FrameworkTests.DeploymentWorkflowTests;
namespace Svm.FrameworkTests;
[Trait("Category","Business")]
public sealed class DeploymentAtomicTests
{
    [Fact]
    public async Task WrappedTaskLockAbortKeepsWorkUnchangedAndCanBeClaimedInFreshScope()
    {
        await using var f = await TaskFixture.CreateAsync();
        foreach (var state in new[] { "40001", "40P01" })
        {
            var selection = await f.SendAsync(new CreateTargetSelectionCommand(Guid.NewGuid(), new(f.SoftwareId, "Filter", new(f.SoftwareId))));
            var id = selection.WorkId!.Value;
            await using (var provider = f.Provider())
            await using (var scope = provider.CreateAsyncScope())
            {
                var tasks = scope.ServiceProvider.GetRequiredService<ITaskWorkflow>();
                var authority = await tasks.AuthorityAsync(id, false, default);
                await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async ct =>
                { await tasks.AcceptAsync(id, authority.DispatchSequence, authority.DispatchEventId, ct); return true; }, default);
            }
            var claim = new ClaimTaskWorkCommand(id, Guid.NewGuid());
            var before = (await f.SendAsync(new GetTaskWorkQuery(id))).Revision;
            var error = await Assert.ThrowsAsync<PersistenceException>(() => f.SendAsync(claim, work: id, interceptor: new WrappedLockAbort(state)));
            Assert.Equal(PersistenceFailure.DependencyUnavailable, error.Failure); Assert.Equal(state, error.SqlState);
            Assert.Equal(before, (await f.SendAsync(new GetTaskWorkQuery(id))).Revision);
            Assert.NotNull((await f.SendAsync(claim, work: id)).Value);
        }
    }
    [Fact]
    public async Task SaveFailureAndCancellationRollBackSelectionAuditResultAndSendingIntent()
    {
        await using var f=await TaskFixture.CreateAsync(); var selection=await f.Count("tsk.target_selections"); var audits=await f.Count("aud.events"); var results=await f.Count("tsk.operation_results"); var outbox=await OutboxFixture.MessagesAsync(f.Database);
        var command=new CreateTargetSelectionCommand(Guid.NewGuid(),new(f.SoftwareId,"Filter",new(f.SoftwareId)));
        var error=await Assert.ThrowsAsync<PersistenceException>(()=>f.SendAsync(command,interceptor:new SaveFailure())); Assert.Equal(PersistenceFailure.DependencyUnavailable,error.Failure);
        Assert.Equal(selection,await f.Count("tsk.target_selections")); Assert.Equal(audits,await f.Count("aud.events")); Assert.Equal(results,await f.Count("tsk.operation_results")); Assert.Equal(outbox,await OutboxFixture.MessagesAsync(f.Database));
        using var source=new CancellationTokenSource(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.SendAsync(command,interceptor:new CancelSave(source),token:source.Token));
        Assert.Equal(selection,await f.Count("tsk.target_selections")); Assert.Equal(audits,await f.Count("aud.events")); Assert.Equal(results,await f.Count("tsk.operation_results")); Assert.Equal(outbox,await OutboxFixture.MessagesAsync(f.Database));
    }
    [Fact]
    public async Task DeniedStartAndReceiptAuditFailureDoNotSaveSnapshotOrTerminalFacts()
    {
        await using var f=await TaskFixture.CreateAsync(); var r=await Formal(f); var i=await Enroll(f); var d=await Deployment(f,r.Id,[i.Id]); await Prepare(f,d.WorkId!.Value); await Tick(f,d.Value.Id);
        var t=Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId,d.Value.Id)))).Items); var a=(await f.SendAsync(new ClaimInstanceTaskCommand(Guid.NewGuid(),t.Id),access:i.Proof,instance:i.Id)).Value;
        var report=InstanceFixture.Report(sequence:2,version:null) with { InstallationState="NotInstalled" };
        await Assert.ThrowsAsync<PersistenceException>(()=>f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),a,new(report,new(PackageFixture.Input.Sha256,true))),access:i.Proof,instance:i.Id,auditFailure:true));
        Assert.Null((await f.SendAsync(new GetInstanceTaskQuery(t.Id))).StartAuthorizedAt); Assert.Equal(1,(await f.SendAsync(new GetInstanceQuery(i.Id))).LastSnapshot!.ReportSeq);
        await f.SendAsync(new StartInstanceTaskCommand(Guid.NewGuid(),a,new(report,new(PackageFixture.Input.Sha256,true))),access:i.Proof,instance:i.Id);
        var receipt=new SubmitTaskReceiptCommand(a,new(Guid.NewGuid(),1,"Terminal",Result:"Succeeded",StateReport:report with { ReportSeq=3,InstallationState="Installed",InstalledVersion=r.Version,InstalledReleaseId=r.Id }));
        await Assert.ThrowsAsync<PersistenceException>(()=>f.SendAsync(receipt,access:i.Proof,instance:i.Id,auditFailure:true));
        Assert.Equal("AwaitingResult",(await f.SendAsync(new GetInstanceTaskQuery(t.Id))).State); Assert.Equal(2,(await f.SendAsync(new GetInstanceQuery(i.Id))).LastSnapshot!.ReportSeq); Assert.Empty((await f.SendAsync(new ListTaskReceiptsQuery(t.Id))).Items);
    }
    private sealed class SaveFailure : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e,InterceptionResult<int> result,CancellationToken ct=default) => throw new IOException("controlled task save failure"); }
    private sealed class WrappedLockAbort(string sqlState) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("tsk.works", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
                throw new InvalidOperationException("controlled EF query wrapper", new PostgresException("controlled task lock abort", "ERROR", "ERROR", sqlState));
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CancelSave(CancellationTokenSource source) : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e,InterceptionResult<int> result,CancellationToken ct=default) { source.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(result); } }
}
