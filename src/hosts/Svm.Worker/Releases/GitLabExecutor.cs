using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;
using Svm.Services.CrossCutting.Pipeline;

namespace Svm.Worker.Releases;

internal sealed class GitLabExecutor(IServiceScopeFactory scopes, ScopedRequestExecutor requests, GitLabImportOptions options,
    PackageFileOptions files, ILogger<GitLabExecutor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var binding in options.Bindings.Where(b => b.Enabled))
            {
                try
                {
                    try { await Send(binding, binding.Id, new ScheduleGitLabScanCommand(binding.SoftwareId), stoppingToken); }
                    catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
                    {
                        await Send(binding, binding.Id, new GetGitLabSourceQuery(binding.SoftwareId), stoppingToken);
                        continue;
                    }
                    IReadOnlyList<Guid> pending;
                    await using (var scope = scopes.CreateAsyncScope()) pending = await scope.ServiceProvider.GetRequiredService<IReleaseImports>().PendingAsync(binding.SoftwareId, stoppingToken);
                    foreach (var id in pending) await Execute(binding, id, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) { logger.LogWarning("GitLab binding {BindingId} scan stopped {FailureType}", binding.Id, error.GetType().Name); }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
    private async Task Execute(GitLabBinding binding, Guid id, CancellationToken token)
    {
        GitLabLease? lease = null; GitLabWork? claimed = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(token); Task? heartbeat = null;
        try
        {
            claimed = (await Send(binding, id, new ClaimGitLabWorkCommand(id, Guid.NewGuid()), token)).Value;
            if (claimed is null) return;
            lease = new(id, claimed.LeaseToken!.Value, claimed.LeaseGeneration);
            heartbeat = Heartbeat(binding, lease, budget, heartbeatStop.Token);
            await using var scope = scopes.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<IGitLabReleaseClient>();
            if (claimed.Kind == "Scan")
            {
                var page = await client.ListAsync(binding, claimed.Page, budget.Token); var releases = new List<GitLabReleaseFacts>();
                var pendingTags = new List<string>();
                foreach (var tag in claimed.InitialScan ? page.Tags : [])
                {
                    await Send(binding, id, new RenewGitLabWorkCommand(lease), token);
                    try
                    {
                        if (await client.VerifyAsync(binding, tag, budget.Token) is { } fact) releases.Add(fact);
                        else pendingTags.Add(tag);
                    }
                    catch (RequestRejectedException e) when (e.Failure is RequestFailure.ValidationFailed or RequestFailure.ResourceNotFound)
                    { logger.LogWarning("GitLab binding {BindingId} contains an ineligible release {Code}", binding.Id, e.Code); }
                }
                await Send(binding, id, new ApplyGitLabScanCommand(lease, releases, page.Tags, pendingTags, page.NextPage), token); return;
            }
            var verified = await client.VerifyAsync(binding, claimed.Tag!, budget.Token);
            if (verified is null) { await Send(binding, id, new DelayGitLabWorkCommand(lease, "PIPELINE_PENDING"), token); return; }
            var storage = scope.ServiceProvider.GetRequiredService<IPackageFiles>();
            await using var mutex = await storage.LockUploadAsync(id, token);
            var begin = (await Send(binding, id, new BeginGitLabImportCommand(lease, verified, Guid.NewGuid(), files.NodeId), token)).Value;
            if (begin.Upload is not { } receipt || receipt.AlreadyReceived) return;
            await using var content = await client.DownloadAsync(binding, verified, budget.Token);
            var lastRenew = DateTimeOffset.MinValue;
            var received = await storage.ReceiveAsync(receipt, content, verified.Manifest.SizeBytes, async ct =>
            {
                if (DateTimeOffset.UtcNow - lastRenew < TimeSpan.FromSeconds(5)) return;
                await Send(binding, id, new RenewGitLabWorkCommand(lease), ct); lastRenew = DateTimeOffset.UtcNow;
            }, budget.Token);
            // Verify source again after download, before committing the sending intent.
            var confirmed = await client.VerifyAsync(binding, claimed.Tag!, budget.Token);
            if (confirmed is null || !GitLabFacts.Same(verified, confirmed)) throw new RequestRejectedException(RequestFailure.VersionConflict);
            await Send(binding, id, new FinishGitLabImportCommand(lease, receipt, received.Size, received.Sha256), token);
        }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        {
            // A fresh authorized scope reads committed progress. No phase Handler is rerun here.
            var observed = await Send(binding, id, new GetGitLabWorkQuery(id), token);
            logger.LogWarning("GitLab work {WorkId} commit observed at {Stage}", id, observed.Stage);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            var code = e is RequestRejectedException rejected ? rejected.Code : "IMPORT_INTERRUPTED";
            if (claimed?.ReleaseId is not null && code == "VALIDATION_FAILED") code = "VERSION_CONFLICT";
            logger.LogWarning("GitLab work {WorkId} paused {Code}", id, code);
            if (lease is null) return;
            try { await Send(binding, id, new DelayGitLabWorkCommand(lease, code), token); }
            catch (PersistenceException unknown) when (unknown.Failure == PersistenceFailure.CommitOutcomeUnknown) { await Send(binding, id, new GetGitLabWorkQuery(id), token); }
            catch (RequestRejectedException) { }
        }
        finally
        {
            heartbeatStop.Cancel(); if (heartbeat is not null) await heartbeat;
        }
    }
    private async Task Heartbeat(GitLabBinding binding, GitLabLease lease, CancellationTokenSource budget, CancellationToken stop)
    {
        try
        {
            while (true) { await Task.Delay(TimeSpan.FromSeconds(15), stop); await Send(binding, lease.WorkId, new RenewGitLabWorkCommand(lease), stop); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (PersistenceException e) when (e.Failure == PersistenceFailure.CommitOutcomeUnknown)
        {
            budget.Cancel();
            try { await Send(binding, lease.WorkId, new GetGitLabWorkQuery(lease.WorkId), stop); } catch (Exception) { }
        }
        catch (Exception) { budget.Cancel(); }
    }
    private async Task<T> Send<T>(GitLabBinding binding, Guid work, IRequest<T> request, CancellationToken token)
    {
        using var identity = GitLabWorkerIdentity.Enter(binding, work, GitLabCapabilities.Owner(request.GetType()));
        return await requests.SendAsync(request, token);
    }
}
