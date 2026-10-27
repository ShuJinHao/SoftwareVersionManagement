using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Svm.EntityFrameworkCore.Framework;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class GitLabSourceManagementTests
{
    internal static GitLabImportOptions Options() => new([], Connections: [new(Guid.NewGuid(), "Allowed GitLab", "https://gitlab.example.invalid/",
        [new(17, "Current project", ["fixture", "other"]), new(18, "Next project", ["fixture"])])]);
    internal static ChangeReleaseSourceCommand Change(PackageFixture f, GitLabImportOptions options, long revision = 0, long project = 17, bool enabled = true) =>
        new(Guid.NewGuid(), f.SoftwareId, options.Sources[0].Id, project, "fixture", enabled, revision, "来源维护夹具");
    internal static async Task Grant(PackageFixture f, bool grant = true)
    {
        var u = await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        var permissions = u.Permissions.Where(p => p.Operation != "release.source.manage").ToList();
        if (grant) permissions.Add(new(f.SoftwareId, "release.source.manage"));
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), u.Id, u.Revision, permissions, "来源夹具显式授权"));
    }
    internal static async Task<T> Send<T>(PackageFixture f, GitLabImportOptions options, IRequest<T> request, GitLabBinding? binding = null,
        Guid? work = null, IInterceptor? interceptor = null, bool auditFailure = false, CancellationToken token = default)
    {
        await using var p = f.Provider(gitLab: options, importer: binding is not null, importBinding: binding, work: work,
            importOwner: GitLabCapabilities.Owner(request.GetType()), interceptor: interceptor, auditFailure: auditFailure);
        await using var scope = p.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, token);
    }
    internal static async Task<GitLabBinding> Binding(PackageFixture f, GitLabImportOptions options)
    {
        await using var p = f.Provider(gitLab: options); await using var scope = p.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IReleaseImports>().ResolveBindingAsync(f.SoftwareId, null, false, default))!;
    }
    [Fact] public async Task ProductPermissionCandidatesPagingAndRevocationAreEnforced()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options();
        await InstanceAccessTests.Rejected(RequestFailure.ResourceNotFound, () => Send(f, options, new GetReleaseSourceOptionsQuery(f.SoftwareId)));
        await Grant(f);
        var page = await Send(f, options, new GetReleaseSourceOptionsQuery(f.SoftwareId, PageSize: 1));
        Assert.Single(page.Items); Assert.Equal(2, page.NextPage); Assert.Equal("Manual", page.Current.Kind);
        Assert.Single((await Send(f, options, new GetReleaseSourceOptionsQuery(f.SoftwareId, "Next"))).Items);
        Assert.DoesNotContain("release.source.manage", RoleOperationCatalog.Field);
        var command = Change(f, options); await Send(f, options, command); await Grant(f, false);
        await InstanceAccessTests.Rejected(RequestFailure.ResourceNotFound, () => Send(f, options, command));
        Assert.Equal(1, await f.Count("rel.source_changes"));
    }
    [Fact] public async Task RebindingRetainsImmutableIdentityAndOriginalReplayWhileConfigCannotOverwrite()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(); await Grant(f);
        var firstCommand = Change(f, options); var first = (await Send(f, options, firstCommand)).Value; var original = await Binding(f, options);
        var next = (await Send(f, options, Change(f, options, first.Revision, 18))).Value;
        Assert.NotEqual(first.BindingId, next.BindingId); Assert.Equal(2, next.Revision);
        var replay = (await Send(f, options, firstCommand)).Value;
        Assert.Equal(first.BindingId, replay.BindingId); Assert.Equal(first.Revision, replay.Revision); Assert.Equal(17, replay.ProjectId);
        Assert.Equal(2, await f.Count("rel.source_bindings")); Assert.Equal(2, await f.Count("rel.source_changes"));
        var withStaleConfig = options with { Bindings = [original with { ConnectionId = null, SourceRevision = 1 }] };
        Assert.Equal(next.BindingId, (await Binding(f, withStaleConfig)).Id);
        await InstanceAccessTests.Rejected(RequestFailure.IdempotencyConflict, () => Send(f, options, firstCommand with { ProjectId = 18 }));
        await InstanceAccessTests.Rejected(RequestFailure.RevisionConflict, () => Send(f, options, Change(f, options, first.Revision)));
        await InstanceAccessTests.Rejected(RequestFailure.PermissionDenied, () => Send(f, options, new ScheduleGitLabScanCommand(f.SoftwareId), original));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => PersistenceDatabase.ExecuteAsync(f.Database.WriterConnection,
            $"UPDATE rel.source_bindings SET \"SubjectId\"=gen_random_uuid() WHERE \"Id\"='{original.Id}'"));
    }
    [Fact] public async Task ActiveWorkBlocksRebindingButDisableDrainsAndNewScopeUsesCurrentSource()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(); await Grant(f);
        var current = (await Send(f, options, Change(f, options))).Value; var original = await Binding(f, options);
        var scan = (await Send(f, options, new ScheduleGitLabScanCommand(f.SoftwareId), original)).Value;
        var failure = await Assert.ThrowsAsync<RequestRejectedException>(() => Send(f, options, Change(f, options, current.Revision, 18)));
        Assert.Equal(RequestFailure.InvalidState, failure.Failure);
        var off = (await Send(f, options, Change(f, options, current.Revision, enabled: false))).Value; Assert.False(off.Enabled);
        await InstanceAccessTests.Rejected(RequestFailure.ConfigurationInvalid, () => Send(f, options, new RequestReleaseSyncCommand(Guid.NewGuid(), f.SoftwareId, "v2.0.0")));
        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(f, options, new ClaimGitLabWorkCommand(scan.Id, Guid.NewGuid()), original, scan.Id)));
        var claimed = Assert.Single(claims, x => x.Value is not null).Value!;
        await Send(f, options, new ApplyGitLabScanCommand(new(scan.Id, claimed.LeaseToken!.Value, claimed.LeaseGeneration), [], [], [], null), original, scan.Id);
        var changed = (await Send(f, options, Change(f, options, off.Revision, 18))).Value;
        var newBinding = await Binding(f, options); Assert.Equal(changed.BindingId, newBinding.Id); Assert.Equal(3, newBinding.SourceRevision);
        await InstanceAccessTests.Rejected(RequestFailure.PermissionDenied, () => Send(f, options, new ScheduleGitLabScanCommand(f.SoftwareId), newBinding with { SourceRevision = 1 }));
        var next = (await Send(f, options, new ScheduleGitLabScanCommand(f.SoftwareId), newBinding)).Value;
        Assert.NotEqual(scan.Id, next.Id);
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task SourceAuditAndOperationResultRollBackTogetherAndLostConfirmationOnlyReplays(string fault)
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(); await Grant(f); var command = Change(f, options);
        var audit = await f.Count("aud.events"); using var cancel = new CancellationTokenSource();
        IInterceptor? interceptor = fault == "save" ? new InstanceAccessTests.SaveFailure() : fault == "cancel" ? new InstanceAccessTests.SaveCancellation(cancel) : null;
        await Assert.ThrowsAnyAsync<Exception>(() => Send(f, options, command, interceptor: interceptor, auditFailure: fault == "audit", token: cancel.Token));
        Assert.Equal(0, await f.Count("rel.source_bindings")); Assert.Equal(0, await f.Count("rel.source_changes")); Assert.Equal(audit, await f.Count("aud.events"));
        var loss = new InstanceAccessTests.LostConfirmation(); var result = await Send(f, options, command, interceptor: loss);
        Assert.Equal(1, loss.Commits); Assert.Equal(1, await f.Count("rel.source_changes"));
        Assert.Equal(result.Value.BindingId, (await Send(f, options, command)).Value.BindingId);
        Assert.Equal(audit + 1, await f.Count("aud.events"));
    }
    [Fact] public async Task ConcurrentEditsHaveOneWinnerAndCandidatesCannotAuthorizeArbitrarySources()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(); await Grant(f);
        var edits = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => { try { await Send(f, options, Change(f, options)); return true; }
            catch (RequestRejectedException e) when (e.Failure == RequestFailure.RevisionConflict) { return false; } }));
        Assert.Single(edits, x => x); Assert.Equal(1, await f.Count("rel.source_changes"));
        await InstanceAccessTests.Rejected(RequestFailure.ConfigurationInvalid, () => Send(f, options, Change(f, options, 1) with { PackageName = "not-allowed" }));
        await InstanceAccessTests.Rejected(RequestFailure.ConfigurationInvalid, () => Send(f, options, Change(f, options, 1) with { ConnectionId = Guid.NewGuid() }));
    }
    [Fact] public async Task HistoricalFormalPackageRemainsAuthorizedAndRepairableAfterRebinding()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(); await Grant(f);
        var first = (await Send(f, options, Change(f, options))).Value; var original = await Binding(f, options);
        var sync = (await Send(f, options, new RequestReleaseSyncCommand(Guid.NewGuid(), f.SoftwareId, "v2.0.0"))).Value;
        var claimed = (await Send(f, options, new ClaimGitLabWorkCommand(sync.Id, Guid.NewGuid()), original, sync.Id)).Value!;
        var lease = new GitLabLease(sync.Id, claimed.LeaseToken!.Value, claimed.LeaseGeneration);
        var upload = (await Send(f, options, new BeginGitLabImportCommand(lease, GitLabImportTests.Fact(), Guid.NewGuid(), "node-a"), original, sync.Id)).Value.Upload!;
        await Send(f, options, new FinishGitLabImportCommand(lease, upload, 123, new string('a', 64)), original, sync.Id);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState, () => Send(f, options, Change(f, options, first.Revision, 18)));
        async Task Accept(Guid id)
        {
            await using var p = f.Provider(gitLab: options); await using var scope = p.CreateAsyncScope();
            var packages = scope.ServiceProvider.GetRequiredService<IPackages>(); var work = await packages.AuthorityAsync(id, false, default);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async ct => { await packages.AcceptAsync(id, work.DispatchSequence, work.DispatchEventId, ct); return true; }, default);
        }
        await Accept(upload.UploadId);
        var initialLease = (await Send(f, options, new ClaimPackageWorkCommand(upload.UploadId, "node-a", Guid.NewGuid()), work: upload.UploadId)).Value!;
        await Send(f, options, new CompletePackageWorkCommand(initialLease, f.Healthy), work: upload.UploadId);
        var before = await f.SendAsync(new GetReleaseQuery(upload.ReleaseId)); Assert.Equal("Formal", before.State);
        await Send(f, options, Change(f, options, first.Revision, 18)); var current = await Binding(f, options);
        await using (var p = f.Provider(gitLab: options))
        await using (var scope = p.CreateAsyncScope())
        {
            var imports = scope.ServiceProvider.GetRequiredService<IReleaseImports>();
            Assert.True(await imports.AuthorizesPackageAsync(upload.ReleaseId, original.SubjectId, f.SoftwareId, default));
            Assert.False(await imports.AuthorizesPackageAsync(upload.ReleaseId, current.SubjectId, f.SoftwareId, default));
        }
        await Send(f, options, new CheckPackageReplicasCommand(upload.PackageId, [f.Healthy[0], new("node-b", "Missing", f.Instances.Clock.GetUtcNow())]), work: upload.PackageId);
        var repair = await PersistenceDatabase.ScalarAsync<Guid>(f.Database.ReaderConnection, "SELECT \"Id\" FROM pkg.works WHERE \"Kind\"='Repair'");
        await Accept(repair); var repairLease = (await Send(f, options, new ClaimPackageWorkCommand(repair, "node-a", Guid.NewGuid()), work: repair)).Value!;
        await Send(f, options, new CompletePackageWorkCommand(repairLease, f.Healthy), work: repair);
        Assert.Equal(before, await f.SendAsync(new GetReleaseQuery(upload.ReleaseId)));
        var second = (await Send(f, options, new RequestReleaseSyncCommand(Guid.NewGuid(), f.SoftwareId, "v2.0.0"))).Value;
        var secondClaim = (await Send(f, options, new ClaimGitLabWorkCommand(second.Id, Guid.NewGuid()), current, second.Id)).Value!;
        var fact = GitLabImportTests.Fact(); fact = fact with { Manifest = fact.Manifest with { ProjectId = 18 } };
        await InstanceAccessTests.Rejected(RequestFailure.VersionConflict, () => Send(f, options, new BeginGitLabImportCommand(new(second.Id, secondClaim.LeaseToken!.Value, secondClaim.LeaseGeneration), fact, Guid.NewGuid(), "node-a"), current, second.Id));
        Assert.Equal(1, await f.Count("rel.releases"));
    }
    [Fact] public async Task LegacyAdoptionPreservesBindingSubjectAndStopsFileOverrides()
    {
        await using var f = await PackageFixture.CreateAsync(); var legacy = GitLabImportTests.Options(f); var options = Options() with { Bindings = legacy.Bindings }; await Grant(f);
        var scan = (await GitLabImportTests.Send(f, legacy, new ScheduleGitLabScanCommand(f.SoftwareId))).Value;
        var claim = (await GitLabImportTests.Send(f, legacy, new ClaimGitLabWorkCommand(scan.Id, Guid.NewGuid()), scan.Id)).Value!;
        await GitLabImportTests.Send(f, legacy, new ApplyGitLabScanCommand(new(scan.Id, claim.LeaseToken!.Value, claim.LeaseGeneration), [], [], [], null), scan.Id);
        var adopted = (await Send(f, options, Change(f, options, 1))).Value;
        Assert.Equal(legacy.Bindings[0].Id, adopted.BindingId); Assert.Equal(legacy.Bindings[0].SubjectId, (await Binding(f, options)).SubjectId);
        await Send(f, options, Change(f, options, adopted.Revision, enabled: false));
        Assert.False((await Binding(f, options)).Enabled);
    }
    [Fact] public async Task ManagedHttpsConnectionPreservesCredentialBoundariesAndDownloadsVerifiedContent()
    {
        await using var fixture = await GitLabProtocolFixture.StartAsync(); var legacy = fixture.Config(Guid.NewGuid()); var old = legacy.Import.Bindings[0];
        var connection = new GitLabConnection(Guid.NewGuid(), "Protocol fixture", old.BaseUri, [new(old.ProjectId, "Project", [old.PackageName])]);
        var options = legacy with { Import = legacy.Import with { Bindings = [], Connections = [connection] }, Credentials = [],
            ConnectionCredentials = [new(connection.Id, fixture.ApiToken, fixture.PackageToken)] };
        options.Validate(); var binding = old with { ConnectionId = connection.Id, SourceRevision = 3, Enabled = false };
        using var client = new GitLabReleaseClient(options, new GitLabProtocolFixture.OutsideUnit());
        var fact = (await client.VerifyAsync(binding, "v2.0.0", default))!;
        await using var stream = await client.DownloadAsync(binding, fact, default); using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes);
        Assert.Equal(fixture.Bytes, bytes.ToArray()); Assert.False(fixture.CredentialLeak);
        await InstanceAccessTests.Rejected(RequestFailure.PermissionDenied, () => client.VerifyAsync(binding with { BaseUri = "https://other.invalid/" }, "v2.0.0", default));
        Assert.DoesNotContain(fixture.ApiToken, options.ConnectionCredentials[0].ToString());
    }
    [Fact] public async Task UpgradePreservesLegacySourceHistoryAndMatchesFrozenModelWithoutGrants()
    {
        await using var db = new PersistenceDatabase(); await db.CreateAsync(false);
        SvmDbContext Context() => new(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection, p => p.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options);
        await using (var c = Context()) await c.GetService<IMigrator>().MigrateAsync("20261024000100_RolesAndSiteScopes");
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, """
            INSERT INTO rel.software VALUES ('11111111-1111-4111-8111-111111111111','MIGRATION-S','Source migration','Vision',NULL,1);
            INSERT INTO rel.source_bindings ("Id","SoftwareId","SubjectId","BaseUri","ProjectId","PackageName","Initialized")
            VALUES ('22222222-2222-4222-8222-222222222222','11111111-1111-4111-8111-111111111111','33333333-3333-4333-8333-333333333333','https://gitlab.example.invalid/',17,'fixture',true);
            """);
        var before = await PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection, "SELECT row_to_json(b)::text FROM rel.source_bindings b");
        var result = await db.Runner.ApplyAsync(default); Assert.Equal(14, result.Applied.Count);
        var after = await PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection, "SELECT row_to_json(b)::text FROM (SELECT \"Id\",\"SoftwareId\",\"SubjectId\",\"BaseUri\",\"ProjectId\",\"PackageName\",\"Initialized\",\"LastSyncedAt\",\"NextScanAt\",\"LatestWorkId\",\"LastErrorCode\" FROM rel.source_bindings) b");
        Assert.Equal(System.Text.Json.Nodes.JsonNode.Parse(before)!.ToJsonString(), System.Text.Json.Nodes.JsonNode.Parse(after)!.ToJsonString());
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM iam.permission_catalog WHERE \"Operation\"='release.source.manage'"));
        Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM iam.permissions WHERE \"Operation\"='release.source.manage'"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection, "SELECT has_table_privilege(current_user,'rel.source_changes','UPDATE')"));
        await using (var c = Context()) Assert.False(c.Database.HasPendingModelChanges());
        var dir = Path.Combine(OutboxFixture.Root, "artifacts/gitlab-source-management"); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "upgrade.sql"), db.Runner.GenerateScript());
    }
}
