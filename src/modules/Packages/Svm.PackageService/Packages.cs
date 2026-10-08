using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Packages;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.PackageService;

public static class PackageRegistration
{
    public static IServiceCollection AddSvmPackages(this IServiceCollection services) => services.AddScoped<IPackages, Packages>();
}
internal sealed class Packages(IPackageRepository repository, IUnitOfWork unit, TimeProvider clock,
    PackageNodeCatalog nodes, PackageLimits limits, PackageExecutionOptions execution) : IPackages
{
    public async Task<Guid?> SoftwareForAsync(string resource, Guid id, bool protect, CancellationToken token)
    {
        if (resource == "package") return (await repository.GetAsync(id, protect, token))?.SoftwareId;
        return (await repository.WorkAsync(id, protect, token))?.SoftwareId;
    }
    public async Task<PackageView> GetAsync(Guid id, bool upload, bool protect, CancellationToken token)
    {
        if (upload) id = (await ExistingWork(id, protect, token)).PackageId;
        var package = await Existing(id, protect, token);
        return await View(package, await repository.UploadAsync(id, protect, token), token);
    }
    public async Task<Guid> UploadIdAsync(Guid id, CancellationToken token) => (await Existing(id, false, token)).UploadId;
    public async Task<PackageWorkView> WorkAsync(Guid id, CancellationToken token)
    {
        var w = await ExistingWork(id, false, token);
        var count = (await repository.ReplicasAsync(w.PackageId, false, token)).Count(x => x.State == "Healthy");
        return new(w.Id.Value, w.Kind, w.State, w.Stage, count, 2, w.LastErrorCode, [], w.CreatedAt, w.CompletedAt, w.Revision);
    }
    public Task<Guid> CreateAsync(Guid id, Guid releaseId, Guid softwareId, Guid initiator, PackageInput input, CancellationToken token)
    {
        RequireWrite(); limits.Validate(); nodes.Validate(); token.ThrowIfCancellationRequested();
        if (input.SizeBytes > limits.MaxPackageBytes) throw new RequestRejectedException(RequestFailure.PayloadTooLarge);
        var p = new PackageAsset(id, releaseId, softwareId, input.FileName, input.SizeBytes, input.Sha256.ToLowerInvariant());
        var w = new PackageWork(p.UploadId, id, softwareId, initiator, clock.GetUtcNow()); repository.Add(p, w);
        foreach (var node in nodes.Nodes) repository.Add(new PackageReplica(Guid.NewGuid(), id, node));
        return Task.FromResult(w.Id.Value);
    }
    public async Task<UploadReceipt> BeginAsync(Guid id, Guid receiveToken, string node, CancellationToken token)
    {
        RequireWrite(); nodes.Require(node); var w = await ExistingWork(id, true, token); var p = await Existing(w.PackageId, true, token);
        if (w.Id.Value != p.UploadId) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (p.Disabled) throw new RequestRejectedException(RequestFailure.InvalidState);
        if (p.SizeBytes is not null && w.Stage is not ("UploadFailed" or "Receiving" or "AwaitingUpload")) return Receipt(w, p, true);
        if (w.SourceNode.Length != 0 && w.SourceNode != node) throw new RequestRejectedException(RequestFailure.InvalidState);
        w.Begin(receiveToken, node); repository.Add(new PackageReceiveAttempt(receiveToken, id, p.Id.Value, w.ReceiveGeneration, node)); return Receipt(w, p, false);
    }
    public async Task<UploadReceipt> ReceiptAsync(Guid id, CancellationToken token)
    {
        var attempt = await repository.AttemptAsync(id, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var w = await ExistingWork(attempt.UploadId, false, token); var p = await Existing(attempt.PackageId, false, token);
        return new(attempt.UploadId, p.Id.Value, p.SoftwareId, p.ReleaseId, id, attempt.Generation, attempt.NodeId,
            p.ExpectedSize, p.ExpectedSha256, w.ReceiveToken != id || w.Stage != "Receiving");
    }
    public async Task<UploadReceipt> ReceivingAsync(Guid id, Guid receiveToken, CancellationToken token)
    {
        var w = await ExistingWork(id, false, token); var p = await Existing(w.PackageId, false, token);
        if (p.Disabled || w.Stage is "Stopped" or "UploadFailed" || w.ReceiveToken != receiveToken) throw new RequestRejectedException(RequestFailure.InvalidState);
        return Receipt(w, p, w.Stage != "Receiving");
    }
    public async Task<PackageWorkAuthority> FinishAsync(UploadReceipt r, long size, string digest, CancellationToken token)
    {
        RequireWrite(); var w = await ExistingWork(r.UploadId, true, token); var p = await Existing(w.PackageId, true, token);
        if (p.Disabled || w.Stage != "Receiving" || w.ReceiveToken != r.ReceiveToken || w.ReceiveGeneration != r.ReceiveGeneration || w.SourceNode != r.SourceNode)
            throw new RequestRejectedException(RequestFailure.InvalidState);
        if (size != p.ExpectedSize || digest != p.ExpectedSha256) throw new RequestRejectedException(RequestFailure.ChecksumMismatch);
        p.Verify(size, digest); w.Dispatch(w.InitiatorId); repository.Add(new PackageDispatch(w.DispatchEventId, w.Id.Value, w.DispatchSequence));
        return Authority(w, p);
    }
    public async Task FailUploadAsync(Guid id, Guid tokenId, string code, CancellationToken token)
    {
        RequireWrite(); var w = await ExistingWork(id, true, token); var p = await Existing(w.PackageId, true, token);
        if (w.ReceiveToken == tokenId && w.Stage == "Receiving" && !p.Disabled) { w.Fail(code, true); p.Fail(); }
    }
    public async Task<PackageWorkAuthority> RetryAsync(Guid id, long revision, Guid initiator, CancellationToken token)
    {
        RequireWrite(); var p = await Existing(id, true, token); var w = await repository.UploadAsync(id, true, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (p.Revision != revision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (p.Disabled || p.SizeBytes is null || p.State != "Failed" || w.Stage != "CopyFailed") throw new RequestRejectedException(RequestFailure.InvalidState);
        p.Retry(); w.Dispatch(initiator); repository.Add(new PackageDispatch(w.DispatchEventId, w.Id.Value, w.DispatchSequence)); return Authority(w, p);
    }
    public async Task StopAsync(Guid id, CancellationToken token)
    {
        RequireWrite(); var p = await Existing(id, true, token); p.Stop();
        var w = await repository.UploadAsync(id, true, token); if (w is not null && w.State != "Completed") w.Stop();
    }
    public async Task<PackageWorkAuthority> AuthorityAsync(Guid id, bool protect, CancellationToken token)
    { var w = await ExistingWork(id, protect, token); return Authority(w, await Existing(w.PackageId, protect, token)); }
    public async Task AcceptAsync(Guid id, long dispatch, Guid eventId, CancellationToken token)
    { RequireWrite(); var w = await ExistingWork(id, true, token); w.Accept(dispatch, eventId); }
    public Task<bool> HasDispatchAsync(Guid id, long dispatch, Guid eventId, CancellationToken token) => repository.HasDispatchAsync(id, dispatch, eventId, token);
    public Task<IReadOnlyList<Guid>> PendingAsync(DateTimeOffset now, int take, CancellationToken token) => repository.PendingAsync(now, take, token);
    public Task<IReadOnlyList<Guid>> ReadyAsync(int take, Guid? after, CancellationToken token) => repository.ReadyAsync(take, after, token);
    public async Task<PackageLease?> ClaimAsync(Guid id, string node, Guid leaseToken, DateTimeOffset now, CancellationToken token)
    {
        RequireWrite(); nodes.Require(node); var w = await ExistingWork(id, true, token); var p = await Existing(w.PackageId, true, token);
        if (p.Disabled) return null;
        return w.Claim(node, leaseToken, now, execution.LeaseSeconds) ? new(Authority(w, p), leaseToken, w.LeaseGeneration) : null;
    }
    public async Task RenewAsync(PackageLease lease, DateTimeOffset now, CancellationToken token)
    { RequireWrite(); var (w, _) = await Owned(lease, token); w.Renew(now, execution.LeaseSeconds); }
    public async Task<PackageView> CompleteAsync(PackageLease lease, IReadOnlyList<ReplicaFact> facts, CancellationToken token)
    {
        RequireWrite(); var (w, p) = await Owned(lease, token); CheckFacts(facts);
        if (facts.Count(x => x.State == "Healthy") != 2) throw new RequestRejectedException(RequestFailure.NoHealthyReplica);
        await SaveFacts(p.Id.Value, facts, token); if (p.State != "Ready") p.Ready(); w.Complete(clock.GetUtcNow()); return await View(p, w, token);
    }
    public async Task FailWorkAsync(PackageLease lease, string code, CancellationToken token)
    { RequireWrite(); var (w, p) = await Owned(lease, token); w.Fail(code); p.Fail(); }
    public async Task<PackageWorkAuthority?> CheckReplicasAsync(Guid id, IReadOnlyList<ReplicaFact> facts, CancellationToken token)
    {
        RequireWrite(); var p = await Existing(id, true, token); CheckFacts(facts); await SaveFacts(id, facts, token);
        if (p.Disabled || p.State != "Ready" || facts.Count(x => x.State == "Healthy") != 1) return null;
        var work = await repository.UploadAsync(id, true, token);
        if (work is { Kind: "Repair", State: "Pending" or "Running" }) return null;
        var w = new PackageWork(Guid.NewGuid(), id, p.SoftwareId, Guid.Empty, clock.GetUtcNow(), "Repair");
        w.RepairFrom(facts.Single(x => x.State == "Healthy").NodeId); w.Dispatch(Guid.Empty); repository.Add(w);
        repository.Add(new PackageDispatch(w.DispatchEventId, w.Id.Value, w.DispatchSequence)); return Authority(w, p);
    }
    public async Task<DownloadAuthorization> AuthorizeDownloadAsync(Guid id, Guid requestId, string node, Guid generation,
        bool head, Guid subject, string actor, string? employee, CancellationToken token)
    {
        RequireWrite(); nodes.Require(node); var p = await Existing(id, true, token);
        if (p.Disabled || p.State != "Ready") throw new RequestRejectedException(RequestFailure.InvalidState);
        var healthy = (await repository.ReplicasAsync(id, true, token)).Where(x => x.State == "Healthy" &&
            x.CheckedAt > clock.GetUtcNow().AddSeconds(-execution.ReplicaCheckSeconds * 2)).OrderByDescending(x => x.NodeId == node).ThenBy(x => x.NodeId).FirstOrDefault();
        if (healthy is null) throw new RequestRejectedException(RequestFailure.NoHealthyReplica);
        var created = false;
        if (!head)
        {
            var existing = await repository.DownloadAsync(requestId, true, token);
            if (existing is not null)
            { if (existing.PackageId != id || existing.SubjectId != subject || existing.ActorKind != actor || existing.NodeId != node || existing.WorkerGeneration != generation)
                    throw new RequestRejectedException(RequestFailure.IdempotencyConflict); }
            else
            {
                // Nginx completion timestamps have millisecond precision. Persist the start
                // at that same resolution so a fast range response in the same millisecond is valid.
                var started = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
                repository.Add(new PackageDownload(requestId, id, node, generation, subject, actor, employee, started)); created = true;
            }
        }
        return new(requestId, id, healthy.NodeId, p.SizeBytes!.Value, p.Sha256!, $"/__svm_replicas/{healthy.NodeId}/{id:D}.bin", created);
    }
    public async Task<bool> EndDownloadAsync(DownloadEnd end, CancellationToken token)
    {
        RequireWrite(); nodes.Require(end.NodeId); var download = await repository.DownloadAsync(end.RequestId, true, token)
            ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (download.NodeId != end.NodeId || download.WorkerGeneration != end.WorkerGeneration) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        if (end.Outcome is not ("Closed" or "Interrupted") || end.BytesSent < 0 || end.EndedAt < download.StartedAt || end.EndedAt > clock.GetUtcNow().AddMinutes(1))
            throw new RequestRejectedException(RequestFailure.ValidationFailed);
        if (download.State != "Open" && (download.State != end.Outcome || download.BytesSent != end.BytesSent || download.EndedAt != end.EndedAt))
            throw new RequestRejectedException(RequestFailure.IdempotencyConflict);
        return download.End(end.Outcome, end.BytesSent, end.EndedAt);
    }
    public async Task<DownloadEnd?> FindDownloadEndAsync(Guid id, string node, Guid generation, CancellationToken token)
    {
        var d = await repository.DownloadAsync(id, false, token);
        if (d is null) return null;
        if (d.NodeId != node || d.WorkerGeneration != generation) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        return d.EndedAt is { } ended ? new(id, d.NodeId, d.WorkerGeneration, ended, d.BytesSent!.Value, d.State) : null;
    }
    private async Task<(PackageWork Work, PackageAsset Package)> Owned(PackageLease lease, CancellationToken token)
    {
        var w = await ExistingWork(lease.Work.WorkId, true, token); var p = await Existing(w.PackageId, true, token);
        if (p.Disabled || w.DispatchSequence != lease.Work.DispatchSequence || !w.Owns(lease.LeaseToken, lease.LeaseGeneration, clock.GetUtcNow()))
            throw new RequestRejectedException(RequestFailure.InvalidState);
        return (w, p);
    }
    private void CheckFacts(IReadOnlyList<ReplicaFact> facts)
    {
        nodes.Validate();
        if (facts.Count != 2 || !facts.Select(x => x.NodeId).Order(StringComparer.Ordinal).SequenceEqual(nodes.Nodes.Order(StringComparer.Ordinal)) ||
            facts.Any(x => x.State is not ("Healthy" or "Missing" or "Suspect") || x.CheckedAt > clock.GetUtcNow().AddSeconds(5) ||
                x.CheckedAt < clock.GetUtcNow().AddSeconds(-execution.LeaseSeconds))) throw new RequestRejectedException(RequestFailure.ValidationFailed);
    }
    private async Task SaveFacts(Guid id, IReadOnlyList<ReplicaFact> facts, CancellationToken token)
    { foreach (var r in await repository.ReplicasAsync(id, true, token)) { var f = facts.Single(x => x.NodeId == r.NodeId); r.Check(f.State, f.CheckedAt); } }
    private async Task<PackageAsset> Existing(Guid id, bool protect, CancellationToken token) =>
        await repository.GetAsync(id, protect, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    private async Task<PackageWork> ExistingWork(Guid id, bool protect, CancellationToken token) =>
        await repository.WorkAsync(id, protect, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    private static UploadReceipt Receipt(PackageWork w, PackageAsset p, bool received) => new(w.Id.Value, p.Id.Value, p.SoftwareId, p.ReleaseId,
        w.ReceiveToken ?? Guid.Empty, w.ReceiveGeneration, w.SourceNode, p.ExpectedSize, p.ExpectedSha256, received);
    private static PackageWorkAuthority Authority(PackageWork w, PackageAsset p) => new(w.Id.Value, p.Id.Value, p.ReleaseId, p.SoftwareId,
        w.InitiatorId, w.Kind, w.DispatchSequence, w.DispatchEventId, w.Accepted, w.State, w.Stage, w.SourceNode, w.LeaseGeneration, w.LeaseToken,
        w.LeaseUntil, p.ExpectedSize, p.ExpectedSha256, w.LeaseNode);
    private async Task<PackageView> View(PackageAsset p, PackageWork? w, CancellationToken token)
    {
        var count = (await repository.ReplicasAsync(p.Id.Value, false, token)).Count(x => x.State == "Healthy" &&
            x.CheckedAt > clock.GetUtcNow().AddSeconds(-execution.ReplicaCheckSeconds * 2));
        var available = !p.Disabled && p.State == "Ready" && count > 0;
        return new(p.Id.Value, p.ReleaseId, p.State, p.SizeBytes, p.Sha256, p.ExpectedSize, p.ExpectedSha256, count,
            available, available ? $"/api/v1/packages/{p.Id.Value:D}/content" : null, w?.Stage ?? "AwaitingUpload", w?.LastErrorCode, p.Revision, p.UploadId, p.FileName);
    }
    private void RequireWrite() { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
