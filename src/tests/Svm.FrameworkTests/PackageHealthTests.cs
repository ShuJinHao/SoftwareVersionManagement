using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Tasks;
using Xunit;
using static Svm.FrameworkTests.InstanceAccessTests;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageHealthTests
{
    [Fact] public async Task PartialHealthFeedsLatestFormalAndExistingTaskAvailability()
    {
        await using var f = await TaskFixture.CreateAsync();
        var release = await DeploymentWorkflowTests.Formal(f);
        var instance = await DeploymentWorkflowTests.Enroll(f);
        var deployment = await DeploymentWorkflowTests.Deployment(f, release.Id, [instance.Id]);
        await DeploymentWorkflowTests.Prepare(f, deployment.WorkId!.Value);
        await DeploymentWorkflowTests.Tick(f, deployment.Value.Id);
        var task = Assert.Single((await f.SendAsync(new ListInstanceTasksQuery(new(f.SoftwareId, deployment.Value.Id)))).Items);
        f.Instances.Clock.Advance(DateTimeOffset.UtcNow - f.Instances.Clock.GetUtcNow());
        await f.Packages.SendAsync(new CheckPackageReplicasCommand(release.PackageId, f.Packages.Healthy), work: release.PackageId);
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.Packages.SendAsync(new CheckPackageReplicasCommand(release.PackageId, [new("node-b", "Missing", f.Instances.Clock.GetUtcNow())]), work: release.PackageId);
        Assert.Equal(release.Id, (await f.SendAsync(new GetSoftwareQuery(f.SoftwareId))).LatestAvailableFormalReleaseId);
        Assert.Null((await f.SendAsync(new GetInstanceTaskQuery(task.Id))).WaitReason);
        await f.Packages.SendAsync(new CheckPackageReplicasCommand(release.PackageId, [new("node-a", "Missing", f.Instances.Clock.GetUtcNow())]), work: release.PackageId);
        Assert.Null((await f.SendAsync(new GetSoftwareQuery(f.SoftwareId))).LatestAvailableFormalReleaseId);
        Assert.Equal("NoHealthyPackage", (await f.SendAsync(new GetInstanceTaskQuery(task.Id))).WaitReason);
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.Packages.SendAsync(new CheckPackageReplicasCommand(release.PackageId, [new("node-a", "Healthy", f.Instances.Clock.GetUtcNow())]), work: release.PackageId);
        Assert.Equal(release.Id, (await f.SendAsync(new GetSoftwareQuery(f.SoftwareId))).LatestAvailableFormalReleaseId);
        Assert.Null((await f.SendAsync(new GetInstanceTaskQuery(task.Id))).WaitReason);
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection, "SELECT count(*) FROM pkg.works WHERE \"Kind\"='Repair' AND \"State\"='Pending'"));
    }
    [Fact] public async Task PartialFactsMergeInTimeOrderDeduplicateRepairAndAuthorizeOnlyVerifiedNode()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value;
        var lease = await f.Lease(r); await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId);
        var id = r.Release.PackageId; var old = f.Instances.Clock.GetUtcNow(); f.Instances.Clock.Advance(TimeSpan.FromSeconds(1));
        var newer = f.Instances.Clock.GetUtcNow();
        var checks = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => f.SendAsync(new CheckPackageReplicasCommand(id, [new("node-b", "Missing", newer)]), work: id)));
        Assert.Single(checks, x => x.Value); Assert.Equal(2, await f.Count("pkg.dispatches"));
        await f.SendAsync(new CheckPackageReplicasCommand(id, [new("node-b", "Healthy", old)]), work: id);
        var p = await f.SendAsync(new GetPackageQuery(id)); Assert.Equal(1, p.HealthyReplicaCount); Assert.True(p.DownloadAvailable);
        var request = new AuthorizeDownloadCommand(id, Guid.NewGuid(), "node-b", Guid.NewGuid(), false, "node-a");
        var download = (await f.SendAsync(request, work: id, role: "Gateway", node: "node-b")).Value; Assert.Equal("node-a", download.NodeId);
        await Rejected(RequestFailure.NoHealthyReplica, () => f.SendAsync(request with { RequestId = Guid.NewGuid(), VerifiedNode = "node-b" }, work: id, role: "Gateway", node: "node-b"));
        Assert.Equal(1, await f.Count("pkg.download_sessions"));
        await Rejected(RequestFailure.ValidationFailed, () => f.SendAsync(request with { VerifiedNode = null }, work: id, role: "Gateway", node: "node-b"));
        await Rejected(RequestFailure.ValidationFailed, () => f.SendAsync(new CheckPackageReplicasCommand(id, [new("other", "Healthy", newer)]), work: id));
    }
    [Fact] public async Task PartialHealthUnknownCommitIsQueriedAndFailedRepairPreservesReady()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value;
        var lease = await f.Lease(r); await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId);
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(1)); var lost = new LostConfirmation(); var id = r.Release.PackageId;
        var error = await Assert.ThrowsAsync<PersistenceException>(() => f.SendAsync(new CheckPackageReplicasCommand(id, [new("node-b", "Missing", f.Instances.Clock.GetUtcNow())]), work: id, interceptor: lost));
        Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, error.Failure);
        Assert.Equal(1, (await f.SendAsync(new InspectReplicaQuery(id), work: id)).HealthyReplicaCount); Assert.Equal(1, lost.Commits);
        var repairId = await PersistenceDatabase.ScalarAsync<Guid>(f.Database.ReaderConnection, "SELECT \"Id\" FROM pkg.works WHERE \"Kind\"='Repair'");
        await using (var provider = f.Provider()) await using (var scope = provider.CreateAsyncScope())
        {
            var packages = scope.ServiceProvider.GetRequiredService<IPackages>(); var work = await packages.AuthorityAsync(repairId, false, default);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async ct => { await packages.AcceptAsync(repairId, work.DispatchSequence, work.DispatchEventId, ct); return true; }, default);
        }
        var repair = (await f.SendAsync(new ClaimPackageWorkCommand(repairId, "node-a", Guid.NewGuid()), work: repairId)).Value!;
        await f.SendAsync(new FailPackageWorkCommand(repair, "REPLICA_COPY_TIMEOUT"), work: repairId);
        var p = await f.SendAsync(new GetPackageQuery(id)); Assert.Equal("Ready", p.State); Assert.True(p.DownloadAvailable);
        Assert.Equal("Test", (await f.SendAsync(new GetReleaseQuery(r.Release.Id))).State);
    }
    [Theory][InlineData(0,20)][InlineData(20,20)][InlineData(15,30)][InlineData(15,2)]
    public void TimeoutBudgetsMustLeaveRoomInsideGatewayWait(int peer, int authorization) =>
        Assert.Equal(RequestFailure.ConfigurationInvalid, Assert.Throws<RequestRejectedException>(() => new PackageExecutionOptions(PeerResponseSeconds: peer, DownloadAuthorizationSeconds: authorization).Validate()).Failure);
}
