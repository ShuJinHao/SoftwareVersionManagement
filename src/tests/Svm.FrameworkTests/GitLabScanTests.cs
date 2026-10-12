using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.Application;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;
using Xunit;
using static Svm.FrameworkTests.GitLabImportTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class GitLabScanTests
{
    [Theory]
    [InlineData("v2.0.0")]
    [InlineData("v1.8.2")]
    public async Task PendingBootstrapTagIsDiscoveredAfterInitializationAndWaitsForSuccessfulPipeline(string pendingTag)
    {
        await using var f = await PackageFixture.CreateAsync();
        await using var source = await GitLabProtocolFixture.StartAsync();
        source.Tags.Clear(); source.Tags.AddRange([pendingTag, "v1.9.0"]);
        var transport = source.Config(f.SoftwareId); var options = transport.Import; var binding = options.Bindings.Single();
        using var client = new GitLabReleaseClient(transport, new GitLabProtocolFixture.OutsideUnit());
        var page = await client.ListAsync(binding, 1, default); Assert.Null(page.NextPage);
        source.PipelineState = "running";
        Assert.Null(await client.VerifyAsync(binding, pendingTag, default));

        var first = await Scan(f, options);
        await Send(f, options, new ApplyGitLabScanCommand(Lease(first), [source.Facts("v1.9.0")], page.Tags, [pendingTag], null), first.Id);
        Assert.True(await Initialized(f)); Assert.Equal(0, await TagCount(f, pendingTag));
        var ready = await Work(f, options, "v1.9.0");
        await Send(f, options, new BeginGitLabImportCommand(ready, source.Facts("v1.9.0"), Guid.NewGuid(), "node-a"), ready.WorkId);
        Assert.Equal(1, await f.Count("rel.releases"));

        f.Instances.Clock.Advance(TimeSpan.FromSeconds(options.PollSeconds + 1));
        var next = await Scan(f, options); Assert.False(next.InitialScan);
        await Send(f, options, new ApplyGitLabScanCommand(Lease(next), [], page.Tags, [], null), next.Id);
        Assert.Equal(1, await TagCount(f, pendingTag));
        var id = await TagId(f, pendingTag);
        var pending = (await Send(f, options, new ClaimGitLabWorkCommand(id, Guid.NewGuid()), id)).Value!;
        Assert.Null(await client.VerifyAsync(binding, pendingTag, default));
        await Send(f, options, new DelayGitLabWorkCommand(Lease(pending), "PIPELINE_PENDING"), id);
        Assert.Equal(1, await f.Count("rel.releases")); Assert.Equal(0, source.Downloads);

        source.PipelineState = "success";
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(options.PollSeconds + 1));
        var resumed = (await Send(f, options, new ClaimGitLabWorkCommand(id, Guid.NewGuid()), id)).Value!;
        var verified = await client.VerifyAsync(binding, pendingTag, default); Assert.NotNull(verified);
        var imported = (await Send(f, options, new BeginGitLabImportCommand(Lease(resumed), verified!, Guid.NewGuid(), "node-a"), id)).Value;
        Assert.Equal(id, imported.Work.Id); Assert.Equal(2, await f.Count("rel.releases"));
        Assert.Equal(1, await TagCount(f, pendingTag)); Assert.False(source.CredentialLeak);
    }

    [Fact]
    public async Task PagingResumesCandidateWithoutArchivingPendingTagAndOldLeaseCannotReplay()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f);
        var first = await Scan(f, options);
        var pageOne = new ApplyGitLabScanCommand(Lease(first), [Fact("v2.9.0")], ["v2.9.0", "v3.0.0"], ["v3.0.0"], 2);
        await Send(f, options, pageOne, first.Id);
        Assert.Equal(0, await TagCount(f, "v3.0.0")); Assert.False(await Initialized(f));

        // Each call builds a fresh provider/scope, so the candidate and page must survive outside tracked memory.
        var resumed = (await Send(f, options, new ClaimGitLabWorkCommand(first.Id, Guid.NewGuid()), first.Id)).Value!;
        Assert.Equal(2, resumed.Page); Assert.Equal("2.9.0", resumed.Candidate!.Manifest.Version);
        Assert.Null((await Send(f, options, new ClaimGitLabWorkCommand(first.Id, Guid.NewGuid()), first.Id)).Value);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState, () => Send(f, options, pageOne, first.Id));
        var finalPage = new ApplyGitLabScanCommand(Lease(resumed), [Fact("v2.10.0"), Fact("v1.8.1")], ["v2.10.0", "v1.8.1"], [], null);
        await Send(f, options, finalPage, first.Id);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState, () => Send(f, options, finalPage, first.Id));
        Assert.True(await Initialized(f)); Assert.Equal("Historical", await TagStage(f, "v2.9.0"));
        Assert.Equal("Pending", await TagStage(f, "v2.10.0")); Assert.Equal(0, await TagCount(f, "v3.0.0"));

        Guid? discovered = null;
        for (var i = 0; i < 2; i++)
        {
            f.Instances.Clock.Advance(TimeSpan.FromSeconds(options.PollSeconds + 1));
            var scan = await Scan(f, options);
            await Send(f, options, new ApplyGitLabScanCommand(Lease(scan), [], ["v2.9.0", "v2.10.0", "v1.8.1", "v3.0.0"], [], null), scan.Id);
            Assert.Equal(1, await TagCount(f, "v3.0.0"));
            var id = await TagId(f, "v3.0.0"); discovered ??= id; Assert.Equal(discovered, id);
            Assert.Equal("Historical", await TagStage(f, "v1.8.1"));
        }
        var historyId = await TagId(f, "v1.8.1");
        Assert.Equal(historyId, (await Work(f, options, "v1.8.1")).WorkId);
        Assert.Equal("Pending", await TagStage(f, "v1.8.1"));
    }

    [Fact]
    public async Task AllPendingLeavesBootstrapUninitializedUntilHighestQualifiedVersionIsKnown()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f);
        string[] tags = ["v2.9.0", "v2.10.0"];
        var first = await Scan(f, options);
        await Send(f, options, new ApplyGitLabScanCommand(Lease(first), [], tags, tags, null), first.Id);
        Assert.False(await Initialized(f)); Assert.Equal(0, await TagCount(f, tags[0])); Assert.Equal(0, await TagCount(f, tags[1]));
        Assert.Equal(0, await f.Count("rel.releases")); Assert.Equal(0, await f.Count("pkg.packages"));
        f.Instances.Clock.Advance(TimeSpan.FromSeconds(options.PollSeconds + 1));
        var next = await Scan(f, options); Assert.True(next.InitialScan);
        await Send(f, options, new ApplyGitLabScanCommand(Lease(next), [Fact(tags[0]), Fact(tags[1])], tags, [], null), next.Id);
        Assert.True(await Initialized(f)); Assert.Equal("Historical", await TagStage(f, tags[0]));
        Assert.Equal("Pending", await TagStage(f, tags[1]));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("duplicate", false)]
    [InlineData("foreign", false)]
    [InlineData("overlap", false)]
    [InlineData("oversize", false)]
    [InlineData("empty", true)]
    [InlineData("limit", true)]
    public async Task ScanHandoffValidatesPendingSet(string mode, bool valid)
    {
        var services = new ServiceCollection(); services.AddSvmPackageWorkerApplication();
        await using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var validator = scope.ServiceProvider.GetRequiredService<IValidator<ApplyGitLabScanCommand>>();
        IReadOnlyList<string> tags = ["v2.0.0"];
        IReadOnlyList<string> pending = mode switch
        {
            "null" => null!, "duplicate" => ["v2.0.0", "v2.0.0"], "foreign" => ["v9.0.0"], "empty" => [],
            "limit" => Enumerable.Range(0, 100).Select(i => $"v3.0.{i}").ToArray(),
            "oversize" => Enumerable.Range(0, 101).Select(i => $"v3.0.{i}").ToArray(), _ => ["v2.0.0"]
        };
        if (mode is "limit" or "oversize") tags = pending.Take(100).ToArray();
        var command = new ApplyGitLabScanCommand(new(Guid.NewGuid(), Guid.NewGuid(), 1), mode == "overlap" ? [Fact()] : [], tags, pending, null);
        var result = await validator.ValidateAsync(command);
        Assert.Equal(valid, result.IsValid);
        Assert.All(result.Errors, error => Assert.Equal("VALIDATION_FAILED", error.ErrorCode));
    }

    private static GitLabLease Lease(GitLabWork work) => new(work.Id, work.LeaseToken!.Value, work.LeaseGeneration);
    private static async Task<GitLabWork> Scan(PackageFixture f, GitLabImportOptions options)
    {
        var scheduled = (await Send(f, options, new ScheduleGitLabScanCommand(f.SoftwareId))).Value;
        return (await Send(f, options, new ClaimGitLabWorkCommand(scheduled.Id, Guid.NewGuid()), scheduled.Id)).Value!;
    }
    private static Task<bool> Initialized(PackageFixture f) => PersistenceDatabase.ScalarAsync<bool>(f.Database.ReaderConnection, "SELECT \"Initialized\" FROM rel.source_bindings");
    private static Task<long> TagCount(PackageFixture f, string tag) => PersistenceDatabase.ScalarAsync<long>(f.Database.ReaderConnection, "SELECT count(*) FROM rel.sync_works WHERE \"Tag\"=@tag", new NpgsqlParameter("tag", tag));
    private static Task<Guid> TagId(PackageFixture f, string tag) => PersistenceDatabase.ScalarAsync<Guid>(f.Database.ReaderConnection, "SELECT \"Id\" FROM rel.sync_works WHERE \"Tag\"=@tag", new NpgsqlParameter("tag", tag));
    private static Task<string> TagStage(PackageFixture f, string tag) => PersistenceDatabase.ScalarAsync<string>(f.Database.ReaderConnection, "SELECT \"Stage\" FROM rel.sync_works WHERE \"Tag\"=@tag", new NpgsqlParameter("tag", tag));
}
