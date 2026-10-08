using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.CrossCutting.Pipeline;

namespace Svm.Worker.Packages;

internal sealed class PackageExecutor(IServiceScopeFactory scopes, ScopedRequestExecutor requests,
    PackageFileOptions options, TimeProvider clock, ILogger<PackageExecutor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextHealth = clock.GetUtcNow(); Guid? healthAfter = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<Guid> pending;
                await using (var scope = scopes.CreateAsyncScope()) pending = await scope.ServiceProvider.GetRequiredService<IPackages>().PendingAsync(clock.GetUtcNow(), options.Execution.WorkBatchSize, stoppingToken);
                foreach (var id in pending) await ExecuteWork(id, stoppingToken);
                await using (var scope = scopes.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<IPackageDownloadLogPump>().PumpAsync(stoppingToken);
                if (clock.GetUtcNow() >= nextHealth)
                {
                    IReadOnlyList<Guid> ready;
                    await using (var scope = scopes.CreateAsyncScope()) ready = await scope.ServiceProvider.GetRequiredService<IPackages>().ReadyAsync(options.Execution.WorkBatchSize, healthAfter, stoppingToken);
                    foreach (var id in ready) await Health(id, stoppingToken);
                    healthAfter = ready.Count == options.Execution.WorkBatchSize ? ready[^1] : null;
                    nextHealth = clock.GetUtcNow().AddSeconds(healthAfter is null ? options.Execution.ReplicaCheckSeconds : options.Execution.PollSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Package scan failed {FailureType}", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(options.Execution.PollSeconds), stoppingToken);
        }
    }
    private async Task ExecuteWork(Guid id, CancellationToken token)
    {
        PackageWorkAuthority before;
        await using (var scope = scopes.CreateAsyncScope()) before = await scope.ServiceProvider.GetRequiredService<IPackages>().AuthorityAsync(id, false, token);
        using var identity = PackageWorkerIdentity.Enter(id, before.SoftwareId);
        var claimToken = Guid.NewGuid(); PackageLease? lease;
        try { lease = (await requests.SendAsync(new ClaimPackageWorkCommand(id, options.NodeId, claimToken), token)).Value; }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        {
            var current = await requests.SendAsync(new GetPackageAuthorityQuery(id), token);
            lease = current.LeaseToken == claimToken && current.LeaseNode == options.NodeId && current.LeaseUntil > clock.GetUtcNow()
                ? new(current, claimToken, current.LeaseGeneration) : null;
        }
        catch (RequestRejectedException e) { logger.LogWarning("Package work {WorkId} not claimable {Code}", id, e.Code); return; }
        if (lease is null) return;
        using var fileBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var heartbeat = Heartbeat(lease, fileBudget, heartbeatStop.Token);
        try
        {
            IReadOnlyList<ReplicaFact> facts;
            await using (var scope = scopes.CreateAsyncScope())
                facts = await scope.ServiceProvider.GetRequiredService<IPackageFiles>().PrepareReplicasAsync(lease, ct => Renew(lease, ct), fileBudget.Token);
            await requests.SendAsync(new CompletePackageWorkCommand(lease, facts), fileBudget.Token);
        }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        {
            // Query only. Never repeat a phase Handler after an unconfirmed commit.
            var observed = await requests.SendAsync(new GetPackageAuthorityQuery(id), token);
            logger.LogWarning("Package work {WorkId} commit verified as {State}", id, observed.State);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || fileBudget.IsCancellationRequested) { }
        catch (Exception e)
        {
            logger.LogWarning("Package work {WorkId} stopped {FailureType}", id, e.GetType().Name);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await requests.SendAsync(new FailPackageWorkCommand(lease, e is RequestRejectedException rejected ? rejected.Code : "REPLICA_COPY_FAILED"), budget.Token); }
            catch (PersistenceException failure) when (failure.Failure == PersistenceFailure.CommitOutcomeUnknown) { await requests.SendAsync(new GetPackageAuthorityQuery(id), budget.Token); }
            catch (Exception failure) { logger.LogWarning("Package failure fact {WorkId} unresolved {FailureType}", id, failure.GetType().Name); }
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }
    private async Task Renew(PackageLease lease, CancellationToken token)
    {
        var expected = clock.GetUtcNow().AddSeconds(options.Execution.LeaseSeconds - 1);
        try { await requests.SendAsync(new RenewPackageWorkCommand(lease), token); }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        {
            var current = await requests.SendAsync(new GetPackageAuthorityQuery(lease.Work.WorkId), token);
            if (current.LeaseToken != lease.LeaseToken || current.LeaseGeneration != lease.LeaseGeneration || current.LeaseUntil < expected)
                throw new RequestRejectedException(RequestFailure.InvalidState);
        }
    }
    private async Task Heartbeat(PackageLease lease, CancellationTokenSource fileBudget, CancellationToken token)
    {
        try
        {
            while (true)
            { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, options.Execution.LeaseSeconds / 3)), token); await Renew(lease, token); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { logger.LogWarning("Package lease {WorkId} lost {FailureType}", lease.Work.WorkId, e.GetType().Name); fileBudget.Cancel(); }
    }
    private async Task Health(Guid id, CancellationToken token)
    {
        PackageView p; Guid software;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var packages = scope.ServiceProvider.GetRequiredService<IPackages>(); p = await packages.GetAsync(id, false, false, token);
            software = (await packages.SoftwareForAsync("package", id, false, token))!.Value;
        }
        using var identity = PackageWorkerIdentity.Enter(id, software);
        IReadOnlyList<ReplicaFact> facts;
        await using (var scope = scopes.CreateAsyncScope()) facts = await scope.ServiceProvider.GetRequiredService<IPackageFiles>().InspectAsync(id, p.ExpectedSize, p.ExpectedSha256, token);
        try { await requests.SendAsync(new CheckPackageReplicasCommand(id, facts), token); }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        { await requests.SendAsync(new InspectReplicaQuery(id), token); }
    }
}
