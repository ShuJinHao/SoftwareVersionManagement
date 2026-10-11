using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class GitLabProtocolTests
{
    [Theory]
    [InlineData("unprotected")][InlineData("project")][InlineData("pipeline-ref")][InlineData("sha")][InlineData("job")][InlineData("asset")][InlineData("manifest")][InlineData("duplicate")]
    [InlineData("registry-hash")][InlineData("registry-duplicate")][InlineData("registry-pipeline")]
    public async Task RawGitLabFactsMustAgreeBeforePackageDownload(string mode)
    {
        await using var fixture = await GitLabProtocolFixture.StartAsync(); fixture.Mode = mode;
        var options = fixture.Config(Guid.NewGuid()); using var client = new GitLabReleaseClient(options, new GitLabProtocolFixture.OutsideUnit());
        await InstanceAccessTests.Rejected(RequestFailure.ValidationFailed, () => client.VerifyAsync(options.Import.Bindings[0], "v2.0.0", default));
        Assert.Equal(0, fixture.Downloads); Assert.False(fixture.CredentialLeak);
    }
    [Fact] public async Task BoundedPagesPendingPipelineAndSeparateCredentialsUseValidatedHttpsOnly()
    {
        await using var fixture = await GitLabProtocolFixture.StartAsync(); fixture.Tags.AddRange(["v1.8.1", "v2.10.0"]);
        var options = fixture.Config(Guid.NewGuid()); options.Validate(); var unit = new GitLabProtocolFixture.OutsideUnit(); using var client = new GitLabReleaseClient(options, unit); var binding = options.Import.Bindings[0];
        var page = await client.ListAsync(binding, 1, default); Assert.Equal(2, page.Tags.Count); Assert.Equal(2, page.NextPage); Assert.Null((await client.ListAsync(binding, 2, default)).NextPage);
        fixture.PipelineState = "running"; Assert.Null(await client.VerifyAsync(binding, "v2.0.0", default)); Assert.Equal(0, fixture.Downloads);
        fixture.PipelineState = "success"; var fact = (await client.VerifyAsync(binding, "v2.0.0", default))!;
        await using (var stream = await client.DownloadAsync(binding, fact, default)) { using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); Assert.Equal(fixture.Bytes, bytes.ToArray()); }
        Assert.False(fixture.CredentialLeak); fixture.Mode = "redirect"; await InstanceAccessTests.Rejected(RequestFailure.DependencyUnavailable, () => client.DownloadAsync(binding, fact, default));
        fixture.Mode = "credential"; await InstanceAccessTests.Rejected(RequestFailure.CredentialInvalid, () => client.VerifyAsync(binding, "v2.0.0", default));
        unit.CurrentOperationId = Guid.NewGuid(); await Assert.ThrowsAsync<PersistenceException>(() => client.ListAsync(binding, 1, default));
        Assert.DoesNotContain(fixture.ApiToken, options.ToString()); Assert.DoesNotContain(fixture.PackageToken, options.Credentials[0].ToString());
    }
    [Theory]
    [InlineData("1.0.0", true)][InlineData("v2.10.1", true)][InlineData("v1.0.0-rc1", false)][InlineData("01.0.0", false)][InlineData("1.0", false)][InlineData("0.1.0", false)][InlineData("2147483648.0.0", false)][InlineData("../v1.0.0", false)]
    public void TagVersionsRetainRawIdentityAndUseNumericOrder(string tag, bool valid) => Assert.Equal(valid, GitLabVersions.TryParse(tag, out _));
}
