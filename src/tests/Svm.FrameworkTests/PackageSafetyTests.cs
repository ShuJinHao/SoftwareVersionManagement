using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using static Svm.FrameworkTests.InstanceAccessTests;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageSafetyTests
{
    [Fact] public async Task OldDispatchCannotAcceptRetriedWorkOrReuseItsLease()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var old = await f.Lease(r); await f.SendAsync(new FailPackageWorkCommand(old, "REPLICA_COPY_FAILED"), work: r.UploadId);
        var package = await f.SendAsync(new GetPackageQuery(r.Release.PackageId)); await f.SendAsync(new RetryPackageCommand(Guid.NewGuid(), package.Id, package.Revision, "原版本重派"));
        await using (var provider = f.Provider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var packages = scope.ServiceProvider.GetRequiredService<IPackages>(); var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteAsync(Guid.NewGuid(), async ct => { await packages.AcceptAsync(r.UploadId, old.Work.DispatchSequence, old.Work.DispatchEventId, ct); return true; }, default));
        }
        var current = await f.SendAsync(new GetPackageAuthorityQuery(r.UploadId), work: r.UploadId); Assert.Equal(old.Work.DispatchSequence + 1, current.DispatchSequence); Assert.False(current.Accepted);
        Assert.Null((await f.SendAsync(new ClaimPackageWorkCommand(r.UploadId, "node-a", Guid.NewGuid()), work: r.UploadId)).Value);
        await Rejected(RequestFailure.InvalidState, () => f.SendAsync(new CompletePackageWorkCommand(old, f.Healthy), work: r.UploadId)); Assert.Equal("Staging", (await f.SendAsync(new GetReleaseQuery(r.Release.Id))).State);
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task FinalTestOpeningRollsBackPackageReleaseAndAuditTogether(string stage)
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r); var audit = await f.Count("aud.events");
        using var cancel = new CancellationTokenSource(); Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? fault = stage == "save" ? new SaveFailure() : stage == "cancel" ? new SaveCancellation(cancel) : null;
        await Assert.ThrowsAnyAsync<Exception>(() => f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId, auditFailure: stage == "audit", interceptor: fault, token: cancel.Token));
        Assert.Equal("Staging", (await f.SendAsync(new GetReleaseQuery(r.Release.Id))).State); Assert.Equal("Running", (await f.SendAsync(new GetPackageAuthorityQuery(r.UploadId), work: r.UploadId)).State); Assert.False((await f.SendAsync(new GetPackageQuery(r.Release.PackageId))).DownloadAvailable); Assert.Equal(audit, await f.Count("aud.events"));
    }
    [Fact] public async Task UnknownFinalCommitIsVerifiedFromNewScopeWithoutRepeatingFinalization()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r); var lost = new LostConfirmation();
        var failure = await Assert.ThrowsAsync<PersistenceException>(() => f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId, interceptor: lost)); Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, failure.Failure);
        Assert.Equal("Completed", (await f.SendAsync(new GetPackageAuthorityQuery(r.UploadId), work: r.UploadId)).State); Assert.Equal("Test", (await f.SendAsync(new GetReleaseQuery(r.Release.Id))).State); Assert.Equal(1, lost.Commits);
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection, "SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.work.completed'"));
    }
    [Fact] public async Task OriginalDownloadProofIsRecheckedForPersonnelRevocationAndIndependentInstanceCredential()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r); await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId);
        var generation = Guid.NewGuid(); var get = new AuthorizeDownloadCommand(r.Release.PackageId, Guid.NewGuid(), "node-a", generation, false);
        await f.SendAsync(get, work: r.Release.PackageId, role: "Gateway"); var u = await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId); await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), u.Id, u.Revision, u.Permissions.Where(p => p.Operation != "software.read").ToArray(), "撤销下载查看"));
        await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(get, work: r.Release.PackageId, role: "Gateway")); Assert.Equal(1, await f.Count("pkg.download_sessions"));
        var e = await f.Instances.EnrollAsync(); await f.SendAsync(get with { RequestId = Guid.NewGuid() }, work: r.Release.PackageId, role: "Gateway", access: e.Proof, instance: e.Registration.InstanceId);
        var credentials = await f.Instances.SendAsync(new ListInstanceCredentialsQuery(e.Registration.InstanceId)); var c = credentials.Items.Single();
        await f.Instances.SendAsync(new RevokeInstanceCredentialCommand(Guid.NewGuid(), c.Id, c.Revision, "验证下载吊销"));
        await Rejected(RequestFailure.CredentialInvalid, () => f.SendAsync(get with { RequestId = Guid.NewGuid() }, work: r.Release.PackageId, role: "Gateway", access: e.Proof, instance: e.Registration.InstanceId)); Assert.Equal(2, await f.Count("pkg.download_sessions"));
    }
    [Fact] public async Task WrongInternalRolesAndAllMissingCopiesCannotAuthorizeDownloads()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r); await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId);
        var get = new AuthorizeDownloadCommand(r.Release.PackageId, Guid.NewGuid(), "node-a", Guid.NewGuid(), false); await Rejected(RequestFailure.PermissionDenied, () => f.SendAsync(get, work: r.Release.PackageId, role: "Peer"));
        await f.SendAsync(new CheckPackageReplicasCommand(r.Release.PackageId, PackageFixture.Nodes.Nodes.Select(n => new ReplicaFact(n, "Missing", f.Instances.Clock.GetUtcNow())).ToArray()), work: r.Release.PackageId);
        await Rejected(RequestFailure.NoHealthyReplica, () => f.SendAsync(get, work: r.Release.PackageId, role: "Gateway")); Assert.Equal(0, await f.Count("pkg.download_sessions")); Assert.Equal("Test", (await f.SendAsync(new GetReleaseQuery(r.Release.Id))).State);
    }
    [Fact] public async Task FastDownloadEndUsesGatewayTimePrecisionAndUnknownCommitOnlyQueriesTheSavedFact()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r); await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId); f.Instances.Clock.Advance(TimeSpan.FromTicks(5100));
        var request = Guid.NewGuid(); var generation = Guid.NewGuid(); await f.SendAsync(new AuthorizeDownloadCommand(r.Release.PackageId, request, "node-a", generation, false), work: r.Release.PackageId, role: "Gateway");
        var end = new DownloadEnd(request, "node-a", generation, DateTimeOffset.FromUnixTimeMilliseconds(f.Instances.Clock.GetUtcNow().ToUnixTimeMilliseconds()), 9, "Closed");
        await Rejected(RequestFailure.ValidationFailed, () => f.SendAsync(new RecordDownloadEndCommand(end with { EndedAt = end.EndedAt.AddMilliseconds(-1) }), work: request, role: "Collector"));
        var lost = new LostConfirmation(); var verified = await f.SendAsync(new RecordDownloadEndCommand(end), work: request, role: "Collector", interceptor: lost); Assert.True(verified.Value);
        Assert.Equal(end, await f.SendAsync(new GetDownloadEndQuery(request, "node-a", generation), work: request, role: "Collector")); Assert.Equal(1, lost.Commits);
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection, "SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.download.ended'"));
    }
}
