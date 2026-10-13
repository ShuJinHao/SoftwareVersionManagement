using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;
using Xunit;
using static Svm.FrameworkTests.GitLabImportTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class GitLabPackageAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchedPackageFinishesAfterSynchronizationIsDisabledOrRemoved(bool remove)
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f);
        var (upload, syncId) = await Dispatched(f, options);
        var stopped = remove ? GitLabImportOptions.Disabled : options with { Bindings = [options.Bindings.Single() with { Enabled = false }] };
        PackageWorkAuthority original;
        await using (var provider = f.Provider(gitLab: stopped))
        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider; var packages = services.GetRequiredService<IPackages>();
            var imports = services.GetRequiredService<IReleaseImports>();
            original = await packages.AuthorityAsync(upload.UploadId, false, default);
            Assert.False(original.Accepted); Assert.False((await imports.SourceAsync(f.SoftwareId, default)).Enabled);
        }
        var message = Message(f, original);
        await AuthorizeAndAccept(f, stopped, message);
        await AuthorizeAndAccept(f, stopped, message);
        await using (var provider = f.Provider(gitLab: stopped))
        await using (var scope = provider.CreateAsyncScope())
        {
            var stale = message with { EventId = Guid.NewGuid(), DispatchSequence = message.DispatchSequence + 1 };
            var rejected = await Assert.ThrowsAsync<IntegrationConsumptionException>(async () =>
                await WorkAuthorizer(scope.ServiceProvider).AuthorizeAsync(stale, ModuleOwner.Packages, false, default));
            Assert.Equal(ConsumptionFailure.Conflict, rejected.Failure);
        }
        await InstanceAccessTests.Rejected(RequestFailure.ConfigurationInvalid, () => Send(f, stopped,
            new RequestReleaseSyncCommand(Guid.NewGuid(), f.SoftwareId, "v2.0.1"), importer: false));
        var claimed = (await Send(f, stopped, new ClaimPackageWorkCommand(upload.UploadId, "node-a", Guid.NewGuid()), upload.UploadId, false)).Value!;
        Assert.Equal(original.DispatchEventId, claimed.Work.DispatchEventId);
        await Send(f, stopped, new CompletePackageWorkCommand(claimed, f.Healthy), upload.UploadId, false);
        var release = await f.SendAsync(new GetReleaseQuery(upload.ReleaseId));
        Assert.Equal("Formal", release.State); Assert.Equal(Fact().Manifest, release.Source!.Manifest);
        Assert.Equal("Completed", (await Send(f, stopped, new GetReleaseSyncWorkQuery(syncId), importer: false)).Stage);
        Assert.Equal(1, await f.Count("rel.releases")); Assert.Equal(1, await f.Count("pkg.dispatches"));
        Assert.Equal(1, await OutboxFixture.MessagesAsync(f.Database));
    }

    [Fact]
    public async Task PersistedSourceRejectsOtherSubjectsSoftwareAndManualReleases()
    {
        await using var f = await PackageFixture.CreateAsync();
        var manual = (await f.SendAsync(f.Create())).Value.Release;
        var options = Options(f); var (upload, _) = await Dispatched(f, options); var binding = options.Bindings.Single();
        var impostor = binding with { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), ProjectId = 18, PackageName = "different" };
        await using var provider = f.Provider(gitLab: options with { Bindings = [impostor] });
        await using var scope = provider.CreateAsyncScope(); var imports = scope.ServiceProvider.GetRequiredService<IReleaseImports>();
        Assert.True(await imports.AuthorizesPackageAsync(upload.ReleaseId, binding.SubjectId, f.SoftwareId, default));
        Assert.False(await imports.AuthorizesPackageAsync(upload.ReleaseId, impostor.SubjectId, f.SoftwareId, default));
        Assert.False(await imports.AuthorizesPackageAsync(upload.ReleaseId, binding.SubjectId, Guid.NewGuid(), default));
        Assert.False(await imports.AuthorizesPackageAsync(manual.Id, binding.SubjectId, f.SoftwareId, default));
        Assert.False(await imports.AuthorizesPackageAsync(Guid.NewGuid(), binding.SubjectId, f.SoftwareId, default));
        await InstanceAccessTests.Rejected(RequestFailure.ConfigurationInvalid, () => Send(f, options with { Bindings = [impostor] },
            new RequestReleaseSyncCommand(Guid.NewGuid(), f.SoftwareId, "v2.0.1"), importer: false));
        Assert.Equal(2, await f.Count("rel.releases"));
    }

    [Fact]
    public async Task DisablingReleaseStillRejectsDispatchedPackageWhenSynchronizationIsStopped()
    {
        await using var f = await PackageFixture.CreateAsync(); var options = Options(f); var (upload, _) = await Dispatched(f, options);
        var release = await f.SendAsync(new GetReleaseQuery(upload.ReleaseId));
        await f.SendAsync(new DisableReleaseCommand(Guid.NewGuid(), release.Id, release.Revision, "停用版本仍应阻止包工作"));
        await using var provider = f.Provider(gitLab: GitLabImportOptions.Disabled); await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider; var work = await services.GetRequiredService<IPackages>().AuthorityAsync(upload.UploadId, false, default);
        var failure = await Assert.ThrowsAsync<IntegrationConsumptionException>(async () =>
            await WorkAuthorizer(services).AuthorizeAsync(Message(f, work), ModuleOwner.Packages, false, default));
        Assert.Equal(ConsumptionFailure.PermissionDenied, failure.Failure); Assert.False(work.Accepted);
    }

    private static async Task<(UploadReceipt Upload, Guid SyncId)> Dispatched(PackageFixture f, GitLabImportOptions options)
    {
        var lease = await Work(f, options);
        var upload = (await Send(f, options, new BeginGitLabImportCommand(lease, Fact(), Guid.NewGuid(), "node-a"), lease.WorkId)).Value.Upload!;
        await Send(f, options, new FinishGitLabImportCommand(lease, upload, Fact().Manifest.SizeBytes, Fact().Manifest.Sha256), lease.WorkId);
        return (upload, lease.WorkId);
    }
    private static PackageWorkAvailableV1 Message(PackageFixture f, PackageWorkAuthority work) => new(work.DispatchEventId,
        f.Instances.Clock.GetUtcNow(), work.WorkId, null, f.Instances.Site.Options.Require().SiteId, work.SoftwareId,
        PackageWorkKind.UploadVerificationAndCopy, work.WorkId, work.DispatchSequence);
    private static async Task AuthorizeAndAccept(PackageFixture f, GitLabImportOptions options, PackageWorkAvailableV1 message)
    {
        await using var provider = f.Provider(gitLab: options); await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider; var authorizer = WorkAuthorizer(services); var packages = services.GetRequiredService<IPackages>();
        await services.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async token =>
        {
            var authority = await authorizer.AuthorizeAsync(message, ModuleOwner.Packages, true, token);
            Assert.Equal(message.WorkId, authority.WorkId); Assert.Equal(f.SoftwareId, authority.SoftwareId);
            await packages.AcceptAsync(message.WorkId, message.DispatchSequence, message.EventId, token);
            return true;
        }, default);
    }
    private static IIntegrationWorkAuthorizer WorkAuthorizer(IServiceProvider services)
    {
        // Exercise the production host authorization port without exposing internals or enabling file/network effects.
        var type = Assembly.Load("Svm.Worker").GetType("Svm.Worker.Packages.PackageWorkAuthorizer", throwOnError: true)!;
        var files = new PackageFileOptions("node-a", "", services.GetRequiredService<IPackageServiceIdentity>().SubjectId,
            "", 0, "", null, "", null, "", "", null, "", PackageFixture.Limits, PackageFixture.Execution, [], []);
        return (IIntegrationWorkAuthorizer)ActivatorUtilities.CreateInstance(services, type, files);
    }
}
