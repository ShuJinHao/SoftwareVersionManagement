using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Xunit;
using static Svm.FrameworkTests.InstanceAccessTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class ReleasePublicationTests
{
    [Theory]
    [InlineData("Stopped")]
    [InlineData("Unknown")]
    public async Task HistoricalInstalledEvidencePublishesWithoutRunningOrHeartbeatFreshness(string running)
    {
        await using var f = await PackageFixture.CreateAsync();
        await PublicationScenario.GrantAsync(f);
        var release = await PublicationScenario.ReadyAsync(f);
        var installation = await PublicationScenario.InstallAsync(f, release, running);
        f.Instances.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("Unknown", (await f.SendAsync(new GetInstanceQuery(installation.Id))).Freshness);
        await f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report(sequence: 2, version: "9.0.0")), access: installation.Proof, instance: installation.Id);
        await PublicationScenario.RefreshReplicasAsync(f, release.PackageId);
        var input = PublicationScenario.Publish(release, installation.Evidence);
        var audit = await f.Count("aud.events"); var results = await f.Count("rel.operation_results");
        var messages = await OutboxFixture.MessagesAsync(f.Database);
        var published = (await f.SendAsync(input)).Value;
        Assert.Equal("Formal", published.State); Assert.Equal(release.Version, published.Version); Assert.Equal(release.PackageId, published.PackageId);
        Assert.Equal(f.Instances.Site.Proof.SubjectId, published.PublishedBy);
        Assert.Equal((await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId)).EmployeeNo, published.PublishedEmployeeNo);
        Assert.Equal(f.Instances.Clock.GetUtcNow(), published.PublishedAt); Assert.Equal(installation.Evidence, published.TestEvidenceId);
        Assert.Equal(input.PublishReason, published.PublishReason); Assert.Equal(input.PublishConclusion, published.PublishConclusion);
        Assert.Equal(audit + 1, await f.Count("aud.events")); Assert.Equal(results + 1, await f.Count("rel.operation_results"));
        Assert.Equal(messages, await OutboxFixture.MessagesAsync(f.Database));
        Assert.Equal("9.0.0", (await f.SendAsync(new GetInstanceQuery(installation.Id))).LastSnapshot!.InstalledVersion);
    }

    [Theory]
    [InlineData("missing", RequestFailure.ResourceNotFound)]
    [InlineData("other-release", RequestFailure.ResourceNotFound)]
    [InlineData("other-software", RequestFailure.ResourceNotFound)]
    [InlineData("not-installed", RequestFailure.InvalidState)]
    [InlineData("wrong-version", RequestFailure.InvalidState)]
    public async Task EvidenceMustMatchInstalledSoftwareReleaseAndVersion(string kind, RequestFailure failure)
    {
        await using var f = await PackageFixture.CreateAsync(); await PublicationScenario.GrantAsync(f);
        var release = await PublicationScenario.ReadyAsync(f); var installation = await PublicationScenario.InstallAsync(f, release);
        var evidence = installation.Evidence;
        // Only fixture-owned historical facts are altered; no production bypass or API is introduced.
        if (kind == "missing") evidence = Guid.NewGuid();
        else
        {
            var (field, value) = kind switch
            {
                "other-release" => ("InstalledReleaseId", (object)Guid.NewGuid()),
                "other-software" => ("SoftwareId", Guid.NewGuid()),
                "not-installed" => ("InstallationState", "Unknown"),
                _ => ("InstalledVersion", "9.9.9")
            };
            await PublicationScenario.SqlAsync(f, $"UPDATE ins.installation_evidence SET \"{field}\"=@value WHERE \"Id\"=@id", new NpgsqlParameter("value", value), new NpgsqlParameter("id", evidence));
        }
        await Rejected(failure, () => f.SendAsync(PublicationScenario.Publish(release, evidence)));
        Assert.Null((await f.SendAsync(new GetReleaseQuery(release.Id))).PublishedAt);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("never-checked")]
    [InlineData("repairing")]
    [InlineData("wrong-node")]
    [InlineData("wrong-hash")]
    public async Task PublicationRequiresBothConfiguredFreshVerifiedCopies(string kind)
    {
        await using var f = await PackageFixture.CreateAsync(); await PublicationScenario.GrantAsync(f);
        var release = await PublicationScenario.ReadyAsync(f); var installation = await PublicationScenario.InstallAsync(f, release);
        if (kind == "wrong-hash")
            await PublicationScenario.SqlAsync(f, "UPDATE pkg.packages SET \"Sha256\"=@value WHERE \"Id\"=@id", new NpgsqlParameter("value", new string('b', 64)), new NpgsqlParameter("id", release.PackageId));
        else if (kind == "expired")
            f.Instances.Clock.Advance(TimeSpan.FromSeconds(PackageFixture.Execution.ReplicaCheckSeconds * 2));
        else if (kind == "never-checked")
            await PublicationScenario.SqlAsync(f, "UPDATE pkg.replicas SET \"CheckedAt\"=NULL WHERE \"PackageId\"=@id AND \"NodeId\"='node-b'", new NpgsqlParameter("id", release.PackageId));
        else
            await PublicationScenario.SqlAsync(f, kind == "wrong-node"
                ? "UPDATE pkg.replicas SET \"NodeId\"='node-c' WHERE \"PackageId\"=@id AND \"NodeId\"='node-b'"
                : "UPDATE pkg.replicas SET \"State\"=@state WHERE \"PackageId\"=@id AND \"NodeId\"='node-b'",
                kind == "wrong-node" ? [new NpgsqlParameter("id", release.PackageId)] : [new NpgsqlParameter("id", release.PackageId), new NpgsqlParameter("state", kind == "missing" ? "Missing" : "Repairing")]);
        await Rejected(RequestFailure.InvalidState, () => f.SendAsync(PublicationScenario.Publish(release, installation.Evidence)));
        Assert.Equal("Test", (await f.SendAsync(new GetReleaseQuery(release.Id))).State);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("save")]
    [InlineData("cancel")]
    public async Task PublicationFailureRollsBackFactsAuditAndResult(string stage)
    {
        await using var f = await PackageFixture.CreateAsync(); await PublicationScenario.GrantAsync(f);
        var release = await PublicationScenario.ReadyAsync(f); var installation = await PublicationScenario.InstallAsync(f, release);
        var audit = await f.Count("aud.events"); var results = await f.Count("rel.operation_results");
        using var cancel = new CancellationTokenSource(); IInterceptor? fault = stage == "save" ? new SaveFailure() : stage == "cancel" ? new SaveCancellation(cancel) : null;
        await Assert.ThrowsAnyAsync<Exception>(() => f.SendAsync(PublicationScenario.Publish(release, installation.Evidence), interceptor: fault, auditFailure: stage == "audit", token: cancel.Token));
        var after = await f.SendAsync(new GetReleaseQuery(release.Id)); Assert.Equal("Test", after.State); Assert.Null(after.PublishedAt); Assert.Null(after.TestEvidenceId);
        Assert.Equal(audit, await f.Count("aud.events")); Assert.Equal(results, await f.Count("rel.operation_results"));
    }

    [Fact]
    public async Task ReplayAndUnknownCommitVerifyOnceButCurrentPermissionStillPrecedesReplay()
    {
        await using var f = await PackageFixture.CreateAsync();
        var release = await PublicationScenario.ReadyAsync(f); var installation = await PublicationScenario.InstallAsync(f, release);
        var input = PublicationScenario.Publish(release, installation.Evidence);
        await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(input));
        await PublicationScenario.GrantAsync(f); var audit = await f.Count("aud.events"); var results = await f.Count("rel.operation_results");
        var loss = new LostConfirmation(); var first = await f.SendAsync(input, interceptor: loss); Assert.Equal(1, loss.Commits);
        Assert.Equal(first, await f.SendAsync(input)); Assert.Equal(audit + 1, await f.Count("aud.events")); Assert.Equal(results + 1, await f.Count("rel.operation_results"));
        await Rejected(RequestFailure.IdempotencyConflict, () => f.SendAsync(input with { PublishConclusion = "changed" }));
        await Rejected(RequestFailure.RevisionConflict, () => f.SendAsync(input with { Key = Guid.NewGuid() }));
        await Rejected(RequestFailure.InvalidState, () => f.SendAsync(input with { Key = Guid.NewGuid(), ExpectedRevision = first.Value.Revision }));
        var user = await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision, user.Permissions.Where(p => p.Operation != "release.publish").ToArray(), "撤销发布权限"));
        await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(input));
        Assert.Equal(audit + 2, await f.Count("aud.events"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPublicationAndDisableAreSerializedByRevision(bool disable)
    {
        await using var f = await PackageFixture.CreateAsync(); await PublicationScenario.GrantAsync(f);
        var release = await PublicationScenario.ReadyAsync(f); var installation = await PublicationScenario.InstallAsync(f, release);
        var input = PublicationScenario.Publish(release, installation.Evidence); var audit = await f.Count("aud.events");
        async Task<bool> Attempt(bool other)
        {
            try
            {
                if (other && disable) await f.SendAsync(new DisableReleaseCommand(Guid.NewGuid(), release.Id, release.Revision, "并发停用"));
                else await f.SendAsync(other ? input with { Key = Guid.NewGuid() } : input);
                return true;
            }
            catch (RequestRejectedException e) when (e.Failure == RequestFailure.RevisionConflict) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(false), Attempt(true)), x => x);
        var after = await f.SendAsync(new GetReleaseQuery(release.Id));
        Assert.Equal(release.Revision + 1, after.Revision); Assert.Equal(audit + 1, await f.Count("aud.events"));
        Assert.Equal(after.State == "Formal", after.PublishedAt is not null);
    }

    [Fact]
    public async Task PublicationFactsAndEvidenceAreProtectedAndDisabledFormalStaysInFormalHistory()
    {
        await using var f = await PackageFixture.CreateAsync(); await PublicationScenario.GrantAsync(f);
        var release = await PublicationScenario.ReadyAsync(f); var installation = await PublicationScenario.InstallAsync(f, release);
        var input = PublicationScenario.Publish(release, installation.Evidence); var published = (await f.SendAsync(input)).Value;
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => PublicationScenario.SqlConnectionAsync(f, "UPDATE rel.releases SET \"PublishConclusion\"='changed' WHERE \"Id\"=@id", [new NpgsqlParameter("id", release.Id)], f.Database.WriterConnection));
        Assert.Equal("23514", immutable.SqlState);
        var reference = await Assert.ThrowsAsync<PostgresException>(() => PublicationScenario.SqlAsync(f, "DELETE FROM ins.installation_evidence WHERE \"Id\"=@id", new NpgsqlParameter("id", installation.Evidence)));
        Assert.Equal("23503", reference.SqlState);
        var disabled = (await f.SendAsync(new DisableReleaseCommand(Guid.NewGuid(), release.Id, published.Revision, "保留发布历史"))).Value;
        Assert.Equal(published.PublishedAt, disabled.PublishedAt); Assert.Equal(published.TestEvidenceId, disabled.TestEvidenceId); Assert.Equal(published.PublishConclusion, disabled.PublishConclusion);
        Assert.Equal("Disabled", (await f.SendAsync(input)).Value.State);
        Assert.Empty((await f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, Channel: "Test")))).Items);
        var formal = (await f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, Channel: "Formal", PageSize: 1)))).Items;
        Assert.Equal(release.Id, Assert.Single(formal).Id);
        await Rejected(RequestFailure.InvalidState, () => f.SendAsync(input with { Key = Guid.NewGuid(), ExpectedRevision = disabled.Revision }));
        var staging = (await f.SendAsync(f.Create())).Value.Release;
        await Rejected(RequestFailure.InvalidState, () => f.SendAsync(PublicationScenario.Publish(staging, installation.Evidence)));
    }
}

internal static class PublicationScenario
{
    internal static async Task GrantAsync(PackageFixture f)
    {
        var user = await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision, user.Permissions.Append(new(f.SoftwareId, "release.publish")).ToArray(), "夹具显式发布授权"));
    }
    internal static async Task<ReleaseView> ReadyAsync(PackageFixture f)
    {
        var r = (await f.SendAsync(f.Create())).Value; var lease = await f.Lease(r);
        await f.SendAsync(new CompletePackageWorkCommand(lease, f.Healthy), work: r.UploadId);
        return await f.SendAsync(new GetReleaseQuery(r.Release.Id));
    }
    internal static async Task<(Guid Id, AccessProof Proof, Guid Evidence)> InstallAsync(PackageFixture f, ReleaseView r, string running = "Running")
    {
        var e = await f.Instances.EnrollAsync(); var id = e.Registration.InstanceId;
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(), 0), access: e.Proof, instance: id);
        var result = await f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report(version: r.Version) with { InstalledReleaseId = r.Id, RunningState = running }), access: e.Proof, instance: id);
        return (id, e.Proof, result.Value.EvidenceId!.Value);
    }
    internal static PublishReleaseCommand Publish(ReleaseView release, Guid evidence) => new(Guid.NewGuid(), release.Id, release.Revision, evidence, "现场测试通过", "安装功能及数据保护检查通过。\n允许转为正式版本。");
    internal static Task RefreshReplicasAsync(PackageFixture f, Guid package) => SqlAsync(f, "UPDATE pkg.replicas SET \"CheckedAt\"=@now WHERE \"PackageId\"=@id", new NpgsqlParameter("now", f.Instances.Clock.GetUtcNow()), new NpgsqlParameter("id", package));
    internal static Task SqlAsync(PackageFixture f, string sql, params NpgsqlParameter[] parameters) => SqlConnectionAsync(f, sql, parameters, f.Database.MigrationConnection);
    internal static async Task SqlConnectionAsync(PackageFixture f, string sql, NpgsqlParameter[] parameters, string connection)
    {
        await using var c = new NpgsqlConnection(connection); await c.OpenAsync();
        await using var command = new NpgsqlCommand(sql, c); command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync();
    }
}
