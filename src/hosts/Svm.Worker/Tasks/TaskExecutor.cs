using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Pipeline;

namespace Svm.Worker.Tasks;

internal sealed class TaskExecutor(IServiceScopeFactory scopes, ScopedRequestExecutor requests, TaskOptions options, ILogger<TaskExecutor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<Guid> pending;
                await using (var scope = scopes.CreateAsyncScope()) pending = await scope.ServiceProvider.GetRequiredService<ITaskWorkflow>().PendingAsync(100, token);
                foreach (var id in pending) await Execute(id, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Task scan failed {FailureType}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(options.PollRetrySeconds!.Value), token);
        }
    }
    private async Task Execute(Guid id, CancellationToken ct)
    {
        TaskWorkAuthority authority;
        await using (var scope = scopes.CreateAsyncScope()) authority = await scope.ServiceProvider.GetRequiredService<ITaskWorkflow>().AuthorityAsync(id, false, ct);
        using var identity = TaskWorkerIdentity.Enter(id, authority.SoftwareId); var leaseToken = Guid.NewGuid(); TaskLease? lease;
        try { lease = (await requests.SendAsync(new ClaimTaskWorkCommand(id, leaseToken), ct)).Value; }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        { var w = await requests.SendAsync(new GetTaskAuthorityQuery(id), ct); lease = w.LeaseToken == leaseToken ? new(w, leaseToken, w.LeaseGeneration) : null; }
        if (lease is null) return;
        try
        {
            if (authority.Kind == "TargetSelection") await requests.SendAsync(new MaterializeTaskTargetsCommand(lease), ct);
            else await requests.SendAsync(new AdvanceTaskWorkCommand(lease), ct);
        }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        { var w = await requests.SendAsync(new GetTaskAuthorityQuery(id), ct); logger.LogWarning("Task work {WorkId} commit observed as {State}", id, w.State); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            logger.LogWarning("Task work {WorkId} stopped {FailureType}", id, e.GetType().Name);
            // Database dependency failures remain claimable after expiry. Deterministic snapshot/control
            // errors are recorded using a fresh root; an unconfirmed commit never enters this path.
            if (e is RequestRejectedException or OperationCanceledException)
            { using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
              try { await requests.SendAsync(new FailTaskWorkCommand(lease, e is RequestRejectedException rejected ? rejected.Code : "SNAPSHOT_TIMEOUT"), budget.Token); }
              catch (Exception failure) when (failure is RequestRejectedException or PersistenceException or OperationCanceledException) { } }
        }
    }
}
