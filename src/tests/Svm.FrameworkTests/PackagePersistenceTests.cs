using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Xunit;
using static Svm.FrameworkTests.InstanceAccessTests;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackagePersistenceTests
{
    [Fact] public async Task NumberingSerializesConcurrentCreatorsAndKeepsFailedUploadNumbers()
    {
        await using var f = await PackageFixture.CreateAsync(); var first = (await f.SendAsync(f.Create("Major"))).Value; Assert.Equal("1.0.0", first.Release.Version);
        var results = await Task.WhenAll(f.SendAsync(f.Create()), f.SendAsync(f.Create())); Assert.Equal(new[] { "1.0.1", "1.0.2" }, results.Select(r => r.Value.Release.Version).Order().ToArray());
        await Rejected(RequestFailure.VersionConflict, () => f.SendAsync(f.Create(expected: "1.0.2")));
        var receipt = (await f.SendAsync(new BeginUploadCommand(first.UploadId, Guid.NewGuid(), "node-a"))).Value; await f.SendAsync(new FailUploadCommand(first.UploadId, receipt.ReceiveToken, "UPLOAD_INTERRUPTED"));
        Assert.Equal("UploadFailed", (await f.SendAsync(new GetPackageQuery(first.Release.PackageId))).ProcessingStage); Assert.Equal("1.1.0", (await f.SendAsync(f.Create("Minor"))).Value.Release.Version); Assert.Equal("2.0.0", (await f.SendAsync(f.Create("Major"))).Value.Release.Version); Assert.Equal(5, await f.Count("rel.releases"));
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task CreateFailureRollsBackReleasePackageWorkAuditAndResult(string stage)
    {
        await using var f = await PackageFixture.CreateAsync(); var audit = await f.Count("aud.events"); var priorResults = await f.Count("rel.operation_results"); using var cancel = new CancellationTokenSource(); Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? fault = stage == "save" ? new SaveFailure() : stage == "cancel" ? new SaveCancellation(cancel) : null;
        await Assert.ThrowsAnyAsync<Exception>(() => f.SendAsync(f.Create(), interceptor: fault, auditFailure: stage == "audit", token: cancel.Token)); foreach (var table in new[] { "rel.releases", "pkg.packages", "pkg.works", "pkg.replicas" }) Assert.Equal(0, await f.Count(table)); Assert.Equal(priorResults, await f.Count("rel.operation_results")); Assert.Equal(audit, await f.Count("aud.events"));
    }
    [Fact] public async Task ReplayPrecedesRevisionButCurrentAuthorizationPrecedesReplayAndUnknownCommitOnlyVerifies()
    {
        await using var f = await PackageFixture.CreateAsync(); var request = f.Create(); var loss = new LostConfirmation(); var first = (await f.SendAsync(request, interceptor: loss)).Value; Assert.Equal(1, loss.Commits); Assert.Equal(first, (await f.SendAsync(request)).Value); Assert.Equal(1, await f.Count("rel.releases")); await Rejected(RequestFailure.IdempotencyConflict, () => f.SendAsync(request with { ChangeSummary = "different" }));
        var disable = new DisableReleaseCommand(Guid.NewGuid(), first.Release.Id, 1, "fixture disable"); await f.SendAsync(disable); Assert.Equal("Disabled", (await f.SendAsync(disable)).Value.State); await Rejected(RequestFailure.RevisionConflict, () => f.SendAsync(disable with { Key = Guid.NewGuid() }));
        var u = await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId); await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), u.Id, u.Revision, u.Permissions.Where(p => p.Operation != "release.upload").ToArray(), "撤销上传权限")); await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(request)); Assert.Equal(1, await f.Count("rel.releases"));
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task ReceiveFinalizationRollsBackSendingIntentAndFencesTogether(string stage)
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var begin = (await f.SendAsync(new BeginUploadCommand(r.UploadId, Guid.NewGuid(), "node-a"))).Value; var audit = await f.Count("aud.events"); using var cancel = new CancellationTokenSource(); Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? fault = stage == "save" ? new SaveFailure() : stage == "cancel" ? new SaveCancellation(cancel) : null; var finish = new FinishUploadCommand(begin, PackageFixture.Input.SizeBytes, PackageFixture.Input.Sha256);
        await Assert.ThrowsAnyAsync<Exception>(() => f.SendAsync(finish, interceptor: fault, auditFailure: stage == "audit", token: cancel.Token)); Assert.Equal(0, await OutboxFixture.MessagesAsync(f.Database)); Assert.Equal(0, await f.Count("pkg.dispatches")); Assert.Equal(audit, await f.Count("aud.events")); Assert.Null((await f.SendAsync(new GetPackageQuery(r.Release.PackageId))).SizeBytes);
        var loss = new LostConfirmation(); await f.SendAsync(finish, interceptor: loss); Assert.Equal(1, loss.Commits); await f.SendAsync(finish); Assert.Equal(1, await OutboxFixture.MessagesAsync(f.Database)); Assert.Equal(1, await f.Count("pkg.dispatches"));
    }
    [Fact] public async Task TwoHealthyCopiesGateTestAndOldLeaseCannotCompleteAfterReclaim()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r); await Rejected(RequestFailure.NoHealthyReplica, () => f.SendAsync(new CompletePackageWorkCommand(lease, [f.Healthy[0], new("node-b", "Missing", f.Instances.Clock.GetUtcNow())]), work: r.UploadId)); Assert.Equal("Staging", (await f.SendAsync(new GetReleaseQuery(r.Release.Id))).State);
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(16)); var newer = (await f.SendAsync(new ClaimPackageWorkCommand(r.UploadId, "node-b", Guid.NewGuid()), work: r.UploadId, node: "node-b")).Value!; Assert.Equal(lease.LeaseGeneration + 1, newer.LeaseGeneration); await Rejected(RequestFailure.InvalidState, () => f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId)); await f.SendAsync(new CompletePackageWorkCommand(newer, f.Healthy), work: r.UploadId, node: "node-b"); Assert.Equal("Test", (await f.SendAsync(new GetReleaseQuery(r.Release.Id))).State); Assert.True((await f.SendAsync(new GetPackageQuery(r.Release.PackageId))).DownloadAvailable); Assert.Empty((await f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, Channel: "Formal")))).Items);
    }
    [Fact] public async Task PageFiltersCurrentPermissionsAndNumericSortBeforeLimit()
    {
        await using var f = await PackageFixture.CreateAsync(); for (var i = 0; i < 12; i++) await f.SendAsync(f.Create()); var page = await f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, PageSize: 2))); Assert.Equal(new[] { "1.0.11", "1.0.10" }, page.Items.Select(x => x.Version)); Assert.NotNull(page.Next); var next = await f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, PageSize: 2, After: page.Next))); Assert.Equal("1.0.9", next.Items[0].Version);
        var unauthorized = await f.Instances.Site.PersonAsync([new(null, "asset.read")]); await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(new ListReleasesQuery(new(f.SoftwareId)), person: unauthorized)); await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(new GetPackageQuery(page.Items[0].PackageId), person: unauthorized));
    }
    [Fact] public async Task InstallationChecksReleaseSoftwareAndVersionAndCreatesOnlyRealEvidence()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r); await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId); var e = await f.Instances.EnrollAsync(); var id = e.Registration.InstanceId; await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(), 0), access: e.Proof, instance: id); var report = InstanceFixture.Report(version: r.Release.Version) with { InstalledReleaseId = r.Release.Id };
        await Rejected(RequestFailure.ValidationFailed, () => f.SendAsync(new SubmitStatusReportCommand(report with { InstalledVersion = "9.9.9" }), access: e.Proof, instance: id)); await f.SendAsync(new SubmitStatusReportCommand(report), access: e.Proof, instance: id); await f.SendAsync(new SubmitStatusReportCommand(report), access: e.Proof, instance: id); Assert.Single((await f.SendAsync(new GetTestEvidenceQuery(r.Release.Id))).Items); Assert.Equal(r.Release.Id, (await f.SendAsync(new GetInstanceQuery(id))).LastSnapshot!.InstalledReleaseId); Assert.Single((await f.SendAsync(new ListClientReleasesQuery(new(f.SoftwareId, Channel: "Test")), access: e.Proof, instance: id)).Items); await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(new GetClientReleaseQuery(Guid.NewGuid()), access: e.Proof, instance: id));
    }
    [Fact] public async Task DownloadSessionsHeadAndTrustedEndAreFencedAndDisablePreventsNewRequests()
    {
        await using var f = await PackageFixture.CreateAsync(); var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r); await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId); var request = Guid.NewGuid(); var generation = Guid.NewGuid(); await f.SendAsync(new AuthorizeDownloadCommand(r.Release.PackageId, request, "node-a", generation, true), work: r.Release.PackageId, role: "Gateway"); Assert.Equal(0, await f.Count("pkg.download_sessions")); var get = new AuthorizeDownloadCommand(r.Release.PackageId, request, "node-a", generation, false); await f.SendAsync(get, work: r.Release.PackageId, role: "Gateway"); await f.SendAsync(get, work: r.Release.PackageId, role: "Gateway"); Assert.Equal(1, await f.Count("pkg.download_sessions")); var end = new DownloadEnd(request, "node-a", generation, f.Instances.Clock.GetUtcNow(), 42, "Closed"); var command = new RecordDownloadEndCommand(end); await f.SendAsync(command, work: request, role: "Collector"); await f.SendAsync(command, work: request, role: "Collector"); Assert.Equal(end, await f.SendAsync(new GetDownloadEndQuery(request, "node-a", generation), work: request, role: "Collector")); Assert.Single((await f.SendAsync(new GetDownloadAuditQuery(f.SoftwareId))).Items); var release = await f.SendAsync(new GetReleaseQuery(r.Release.Id)); await f.SendAsync(new DisableReleaseCommand(Guid.NewGuid(), release.Id, release.Revision, "fixture stop")); await Rejected(RequestFailure.InvalidState, () => f.SendAsync(get with { RequestId = Guid.NewGuid() }, work: r.Release.PackageId, role: "Gateway"));
    }
}
