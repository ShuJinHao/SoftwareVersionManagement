using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.FileStorage;

internal sealed partial class PackageFiles
{
    public async Task DeleteNodeAsync(string nodeId, PackageLease lease, Func<CancellationToken, Task> verify, CancellationToken token)
    {
        Outside();
        if (!options.Nodes.Any(n => n.NodeId == nodeId) || lease.Work.Kind != "Cleanup") throw new RequestRejectedException(RequestFailure.PermissionDenied);
        await verify(token);
        if (nodeId == NodeId) { await DeleteLocalAsync(lease.Work, verify, token); return; }
        using var request = new HttpRequestMessage(HttpMethod.Delete, Address(nodeId,
            $"internal/v1/package-work/{lease.Work.WorkId:D}/files?dispatch={lease.Work.DispatchSequence}&leaseGeneration={lease.LeaseGeneration}&leaseToken={lease.LeaseToken:D}"));
        using var transfer = new PackageTransfer(options.Execution.PeerResponseSeconds, options.Limits.UploadIdleSeconds, token);
        using var response = await transfer.SendAsync(peer.Client, request); response.EnsureSuccessStatusCode();
        await verify(token);
    }
    public async Task DeleteLocalAsync(PackageWorkAuthority work, Func<CancellationToken, Task> verify, CancellationToken token)
    {
        Outside();
        if (work.Kind != "Cleanup" || work.PackageId == Guid.Empty || work.UploadId == Guid.Empty) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        // The upload mutex waits for a stopped receiver to release its stream. The package
        // mutex fences copies, repairs and renames. A lease expiry alone proves neither.
        await using var upload = await Lock("upload-" + work.UploadId.ToString("D"), true, token);
        await using var package = await Lock("package-" + work.PackageId.ToString("D"), true, token);
        await verify(token);
        foreach (var folder in new[] { "replicas", "staging", "quarantine" })
        {
            var directory = Path.GetDirectoryName(Location(folder, work.PackageId.ToString("D") + ".bin"))!;
            foreach (var id in new[] { work.PackageId, work.UploadId }.Distinct())
            foreach (var file in Directory.EnumerateFiles(directory, id.ToString("D") + ".*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested(); RejectTree(directory); RejectLink(file);
                await verify(token); File.Delete(file);
            }
        }
        await verify(token);
        // Locks are retained as stable mutex identities. They are not package content.
    }
}
