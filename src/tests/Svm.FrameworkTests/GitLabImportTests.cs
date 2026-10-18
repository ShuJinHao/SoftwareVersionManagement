using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;
using Svm.Services.Contracts.Identity;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class GitLabImportTests
{
    internal static GitLabImportOptions Options(PackageFixture f) => new([new(Guid.NewGuid(), f.SoftwareId, Guid.NewGuid(), "https://gitlab.example.invalid/", 17, "fixture")]);
    internal static GitLabReleaseFacts Fact(string tag = "v2.0.0", string? sha = null) => new(new(1, 17, tag, tag.TrimStart('v'), sha ?? new string('b', 40), 3, 4, "fixture", tag, "app.zip", 123, new string('a', 64)), "正式构建夹具", "https://gitlab.example.invalid/group/project/-/pipelines/3", DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
    internal static async Task<T> Send<T>(PackageFixture f, GitLabImportOptions options, IRequest<T> request, Guid? work = null, bool importer = true, IInterceptor? interceptor = null, bool auditFailure = false, CancellationToken token = default)
    {
        await using var provider = f.Provider(work: work, gitLab: options, importer: importer, importOwner: GitLabCapabilities.Owner(request.GetType()), interceptor: interceptor, auditFailure: auditFailure);
        await using var scope = provider.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, token);
    }
    internal static async Task<GitLabLease> Work(PackageFixture f, GitLabImportOptions options, string tag = "v2.0.0")
    {
        var scheduled = (await Send(f, options, new RequestReleaseSyncCommand(Guid.NewGuid(), f.SoftwareId, tag), importer: false)).Value;
        var work = (await Send(f, options, new ClaimGitLabWorkCommand(scheduled.Id, Guid.NewGuid()), scheduled.Id)).Value!;
        return new(work.Id, work.LeaseToken!.Value, work.LeaseGeneration);
    }
    [Fact] public async Task ImportRegistersOnceCommitsOutboxAndOpensFormalWithoutHumanEvidence()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f); var lease = await Work(f, options);
        var begin = (await Send(f, options, new BeginGitLabImportCommand(lease, Fact(), Guid.NewGuid(), "node-a"), lease.WorkId)).Value;
        var upload = begin.Upload!;
        Assert.Equal(1, await f.Count("rel.releases")); Assert.Equal(0, await f.Count("pkg.dispatches"));
        await Send(f, options, new FinishGitLabImportCommand(lease, upload, 123, new string('a', 64)), lease.WorkId);
        Assert.Equal(1, await f.Count("pkg.dispatches")); Assert.True(await OutboxFixture.MessagesAsync(f.Database) > 0);
        await using (var p = f.Provider(gitLab: options))
        await using (var scope = p.CreateAsyncScope())
        {
            var packages = scope.ServiceProvider.GetRequiredService<IPackages>(); var w = await packages.AuthorityAsync(upload.UploadId, false, default);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async ct => { await packages.AcceptAsync(w.WorkId, w.DispatchSequence, w.DispatchEventId, ct); return true; }, default);
        }
        var packageLease = (await Send(f, options, new ClaimPackageWorkCommand(upload.UploadId, "node-a", Guid.NewGuid()), upload.UploadId, false)).Value!;
        await Send(f, options, new CompletePackageWorkCommand(packageLease, f.Healthy), upload.UploadId, false);
        var release = await f.SendAsync(new GetReleaseQuery(upload.ReleaseId));
        Assert.Equal("Formal", release.State); Assert.Equal("GitLab", release.SourceKind); Assert.Null(release.TestEvidenceId); Assert.Null(release.PublishedBy); Assert.NotNull(release.PublishedAt);
        Assert.Equal(Fact().Manifest, release.Source!.Manifest);
        Assert.Equal("Completed", (await Send(f, options, new GetReleaseSyncWorkQuery(lease.WorkId), importer: false)).Stage);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState, () => Send(f, options, f.Create(), importer: false));
        // Same Tag checks the immutable source but creates neither a version nor another sending intent.
        var again = await Work(f, options);
        var repeated = (await Send(f, options, new BeginGitLabImportCommand(again, Fact(), Guid.NewGuid(), "node-b"), again.WorkId)).Value;
        Assert.Null(repeated.Upload); Assert.Equal(1, await f.Count("rel.releases")); Assert.Equal(1, await f.Count("pkg.dispatches"));
    }
    [Fact] public async Task ConcurrentClaimsLeaseFencesAndInterruptedDownloadRetainVersion()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f); var lease = await Work(f, options);
        Assert.Null((await Send(f, options, new ClaimGitLabWorkCommand(lease.WorkId, Guid.NewGuid()), lease.WorkId)).Value);
        var original = (await Send(f, options, new BeginGitLabImportCommand(lease, Fact(), Guid.NewGuid(), "node-a"), lease.WorkId)).Value;
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(options.LeaseSeconds + 1));
        var claimed = (await Send(f, options, new ClaimGitLabWorkCommand(lease.WorkId, Guid.NewGuid()), lease.WorkId)).Value!;
        var replacement = new GitLabLease(lease.WorkId, claimed.LeaseToken!.Value, claimed.LeaseGeneration);
        var restarted = (await Send(f, options, new BeginGitLabImportCommand(replacement, Fact(), Guid.NewGuid(), "node-b"), lease.WorkId)).Value;
        Assert.Equal(original.Upload!.ReleaseId, restarted.Upload!.ReleaseId); Assert.Equal("node-b", restarted.Upload.SourceNode);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState, () => Send(f, options, new FinishGitLabImportCommand(lease, original.Upload, 123, new string('a', 64)), lease.WorkId));
        Assert.Equal(0, await f.Count("pkg.dispatches")); Assert.Equal(1, await f.Count("rel.releases"));
    }
    [Fact] public async Task ConflictingTagVersionAndAuditFailureCannotPartiallyRegister()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f); var lease = await Work(f, options);
        Assert.Equal(PersistenceFailure.DependencyUnavailable, (await Assert.ThrowsAsync<PersistenceException>(() => Send(f, options, new BeginGitLabImportCommand(lease, Fact(), Guid.NewGuid(), "node-a"), lease.WorkId, auditFailure: true))).Failure);
        Assert.Equal(0, await f.Count("rel.releases")); Assert.Equal(0, await f.Count("pkg.packages"));
        await Send(f, options, new BeginGitLabImportCommand(lease, Fact(), Guid.NewGuid(), "node-a"), lease.WorkId);
        await InstanceAccessTests.Rejected(RequestFailure.VersionConflict, () => Send(f, options, new BeginGitLabImportCommand(lease, Fact(sha: new string('c', 40)), Guid.NewGuid(), "node-a"), lease.WorkId));
        var alias = await Work(f, options, "2.0.0");
        await InstanceAccessTests.Rejected(RequestFailure.VersionConflict, () => Send(f, options, new BeginGitLabImportCommand(alias, Fact("2.0.0"), Guid.NewGuid(), "node-a"), alias.WorkId));
        Assert.Equal(1, await f.Count("rel.releases"));
    }
    [Fact] public async Task BootstrapChoosesNumericLatestAndSubsequentScanIncludesMaintenancePatch()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f);
        var scan = (await Send(f, options, new ScheduleGitLabScanCommand(f.SoftwareId))).Value;
        var first = (await Send(f, options, new ClaimGitLabWorkCommand(scan.Id, Guid.NewGuid()), scan.Id)).Value!;
        await Send(f, options, new ApplyGitLabScanCommand(new(scan.Id, first.LeaseToken!.Value, first.LeaseGeneration), [Fact("v2.9.0"), Fact("v2.10.0")], ["v2.9.0", "v2.10.0"], [], 2), scan.Id);
        var second = (await Send(f, options, new ClaimGitLabWorkCommand(scan.Id, Guid.NewGuid()), scan.Id)).Value!;
        Assert.Equal(2, second.Page);
        await Send(f, options, new ApplyGitLabScanCommand(new(scan.Id, second.LeaseToken!.Value, second.LeaseGeneration), [Fact("v1.8.1")], ["v1.8.1"], [], null), scan.Id);
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection, "SELECT count(*) FROM rel.sync_works WHERE \"Tag\"='v2.10.0'"));
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection, "SELECT count(*) FROM rel.sync_works WHERE \"Tag\"='v1.8.1' AND \"Stage\"='Historical'"));
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(options.PollSeconds + 1));
        var next = (await Send(f, options, new ScheduleGitLabScanCommand(f.SoftwareId))).Value;
        var claimed = (await Send(f, options, new ClaimGitLabWorkCommand(next.Id, Guid.NewGuid()), next.Id)).Value!;
        await Send(f, options, new ApplyGitLabScanCommand(new(next.Id, claimed.LeaseToken!.Value, claimed.LeaseGeneration), [], ["v1.8.1", "v2.9.0", "v2.10.0", "v1.8.2"], [], null), next.Id);
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection, "SELECT count(*) FROM rel.sync_works WHERE \"Tag\"='v1.8.2'"));
        Assert.Equal(2, await PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection, "SELECT count(*) FROM rel.sync_works WHERE \"Stage\"='Historical'"));
        var history = await Work(f, options, "v1.8.1");
        Assert.NotEqual(Guid.Empty, history.Token);
    }
    [Fact] public async Task ManualSyncReplayCurrentAuthorizationAndFrozenModelRemainConsistent()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f); var command = new RequestReleaseSyncCommand(Guid.NewGuid(), f.SoftwareId, "v2.0.0");
        var first = await Send(f, options, command, importer: false); var repeated = await Send(f, options, command, importer: false);
        Assert.Equal(first.Value.Id, repeated.Value.Id); Assert.Equal(1, await f.Count("rel.sync_works"));
        await InstanceAccessTests.Rejected(RequestFailure.IdempotencyConflict, () => Send(f, options, command with { Tag = "v3.0.0" }, importer: false));
        await using var provider = f.Provider(gitLab: options); await using var scope = provider.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<SvmDbContext>().Database.HasPendingModelChanges());
        var script = f.Database.Runner.GenerateScript(); var directory = Path.Combine(OutboxFixture.Root, "artifacts/gitlab-import"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "gitlab-import-upgrade.sql"), script);
        var user = await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision,
            user.Permissions.Where(p => p.Operation != "release.upload").ToArray(), "撤销导入权限"));
        await InstanceAccessTests.Rejected(RequestFailure.ResourceNotFound, () => Send(f, options, command, importer: false));
    }
    [Fact] public async Task ImportSaveFailureRollsBackAndLostCommitIsOnlyQueriedInNewScope()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f); var lease = await Work(f, options);
        var command = new BeginGitLabImportCommand(lease, Fact(), Guid.NewGuid(), "node-a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(f, options, command, lease.WorkId, interceptor: new InstanceAccessTests.SaveFailure()));
        Assert.Equal(0, await f.Count("rel.releases")); Assert.Equal(0, await f.Count("pkg.packages"));
        var loss = new InstanceAccessTests.LostConfirmation();
        var failure = await Assert.ThrowsAsync<PersistenceException>(() => Send(f, options, command, lease.WorkId, interceptor: loss));
        Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, failure.Failure); Assert.Equal(1, loss.Commits);
        var found = await Send(f, options, new GetGitLabWorkQuery(lease.WorkId), lease.WorkId);
        Assert.NotNull(found.ReleaseId); Assert.Equal(command.ReceiveToken, found.ReceiveToken);
        Assert.Equal(1, await f.Count("rel.releases")); Assert.Equal(1, await f.Count("pkg.packages")); Assert.Equal(0, await f.Count("pkg.dispatches"));
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task ImportedReceiveRollsBackAuditWorkAndSendingIntentTogether(string fault)
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f); var lease = await Work(f, options);
        var receipt = (await Send(f, options, new BeginGitLabImportCommand(lease, Fact(), Guid.NewGuid(), "node-a"), lease.WorkId)).Value.Upload!;
        var audit = await f.Count("aud.events"); using var cancel = new CancellationTokenSource();
        IInterceptor? interceptor = fault == "save" ? new InstanceAccessTests.SaveFailure() : fault == "cancel" ? new InstanceAccessTests.SaveCancellation(cancel) : null;
        var command = new FinishGitLabImportCommand(lease, receipt, 123, new string('a', 64));
        await Assert.ThrowsAnyAsync<Exception>(() => Send(f, options, command, lease.WorkId, interceptor: interceptor, auditFailure: fault == "audit", token: cancel.Token));
        Assert.Equal(audit, await f.Count("aud.events")); Assert.Equal(0, await f.Count("pkg.dispatches")); Assert.Equal(0, await OutboxFixture.MessagesAsync(f.Database));
        Assert.Null((await f.SendAsync(new GetPackageQuery(receipt.PackageId))).SizeBytes);
        var loss = new InstanceAccessTests.LostConfirmation();
        Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, (await Assert.ThrowsAsync<PersistenceException>(() => Send(f, options, command, lease.WorkId, interceptor: loss))).Failure);
        Assert.Equal("PackageProcessing", (await Send(f, options, new GetGitLabWorkQuery(lease.WorkId), lease.WorkId)).Stage);
        Assert.Equal(1, loss.Commits); Assert.Equal(1, await f.Count("pkg.dispatches")); Assert.Equal(1, await OutboxFixture.MessagesAsync(f.Database));
    }
    [Fact] public async Task ManualFormalReplicaRepairPreservesPublicationAndEvidence()
    {
        await using var f = await PackageFixture.CreateAsync(); await PublicationScenario.GrantAsync(f);
        var ready = await PublicationScenario.ReadyAsync(f); var install = await PublicationScenario.InstallAsync(f, ready);
        var published = (await f.SendAsync(PublicationScenario.Publish(ready, install.Evidence))).Value;
        await f.SendAsync(new CheckPackageReplicasCommand(ready.PackageId, [f.Healthy[0], new("node-b", "Missing", f.Instances.Clock.GetUtcNow())]), work: ready.PackageId);
        var id = await PersistenceDatabase.ScalarAsync<Guid>(f.Database.ReaderConnection, "SELECT \"Id\" FROM pkg.works WHERE \"Kind\"='Repair'");
        await using (var p = f.Provider())
        await using (var scope = p.CreateAsyncScope())
        {
            var packages = scope.ServiceProvider.GetRequiredService<IPackages>(); var work = await packages.AuthorityAsync(id, false, default);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async ct => { await packages.AcceptAsync(id, work.DispatchSequence, work.DispatchEventId, ct); return true; }, default);
        }
        // This assertion reads SQL availability using database time. Refresh the fake
        // observation clock before the new repair, not the historical publication fact.
        f.Instances.Clock.Advance(DateTimeOffset.UtcNow - f.Instances.Clock.GetUtcNow());
        var lease = (await f.SendAsync(new ClaimPackageWorkCommand(id, "node-a", Guid.NewGuid()), work: id)).Value!;
        await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: id);
        var after = await f.SendAsync(new GetReleaseQuery(ready.Id)); Assert.Equal(published, after);
        Assert.True((await f.SendAsync(new GetPackageQuery(ready.PackageId))).DownloadAvailable);
    }
}
