using Npgsql;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Xunit;
using static Svm.FrameworkTests.InstanceAccessTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class FormalReleaseQueryTests
{
    [Fact]
    public async Task NumericLatestIsSharedAcrossViewsAndFallsBackAfterDisableOrReplicaFailure()
    {
        await using var f = await PackageFixture.CreateAsync(); await PublicationScenario.GrantAsync(f);
        for (var i = 0; i < 2; i++) await f.SendAsync(f.Create());
        var lower = await PublicationScenario.ReadyAsync(f); Assert.Equal("1.0.2", lower.Version);
        for (var i = 0; i < 7; i++) await f.SendAsync(f.Create());
        var higher = await PublicationScenario.ReadyAsync(f); Assert.Equal("1.0.10", higher.Version);
        var lowInstallation = await PublicationScenario.InstallAsync(f, lower); var highInstallation = await PublicationScenario.InstallAsync(f, higher);
        var high = (await f.SendAsync(PublicationScenario.Publish(higher, highInstallation.Evidence))).Value;
        var low = (await f.SendAsync(PublicationScenario.Publish(lower, lowInstallation.Evidence))).Value;
        await f.SendAsync(f.Create()); // A numerically newer Staging release does not replace Formal.
        await PublicationScenario.SqlAsync(f, "UPDATE pkg.replicas SET \"CheckedAt\"=clock_timestamp()");
        var unregistered = (await f.Instances.Site.DeviceAsync("LATEST-P", "LATEST-D")).Device;
        await f.Instances.Site.SendAsync(new CreateBindingCommand(Guid.NewGuid(), unregistered.Id, f.SoftwareId, "最新版本未登记展示"));
        Assert.Equal(high.Id, (await f.SendAsync(new GetSoftwareQuery(f.SoftwareId))).LatestAvailableFormalReleaseId);
        Assert.Equal(high.Id, Assert.Single((await f.SendAsync(new ListSoftwareQuery(new(new(), 50)))).Items).LatestAvailableFormalReleaseId);
        var instance = await f.SendAsync(new GetInstanceQuery(lowInstallation.Id)); Assert.Equal(high.Id, instance.LatestAvailableFormalReleaseId);
        Assert.Equal(lower.Id, instance.LastSnapshot!.InstalledReleaseId); Assert.Equal("1.0.2", instance.LastSnapshot.InstalledVersion);
        Assert.All((await f.SendAsync(new ListInstancesQuery(new(new(f.SoftwareId))))).Items, x => Assert.Equal(high.Id, x.LatestAvailableFormalReleaseId));
        var inventory = (await f.SendAsync(new GetDeviceInventoryQuery(f.Instances.Device.Id, new(new(), 50)))).Items;
        Assert.Equal(2, inventory.Count); Assert.All(inventory, x => { Assert.Equal(high.Id, x.Software.LatestAvailableFormalReleaseId); Assert.Equal(high.Id, x.Instance!.LatestAvailableFormalReleaseId); });
        var unmapped = Assert.Single((await f.SendAsync(new GetDeviceInventoryQuery(unregistered.Id, new(new(), 50)))).Items);
        Assert.Null(unmapped.Instance); Assert.Equal(high.Id, unmapped.Software.LatestAvailableFormalReleaseId);
        Assert.Equal(high.Id, (await f.SendAsync(new GetClientContextQuery(), access: lowInstallation.Proof, instance: lowInstallation.Id)).LatestAvailableFormalReleaseId);
        var client = await f.SendAsync(new ListClientReleasesQuery(new(f.SoftwareId)), access: lowInstallation.Proof, instance: lowInstallation.Id);
        Assert.Equal(new[] { high.Id, low.Id }, client.Items.Select(x => x.Id));

        await PublicationScenario.SqlAsync(f, "UPDATE pkg.replicas SET \"State\"='Missing' WHERE \"PackageId\"=@id AND \"NodeId\"='node-b'", new NpgsqlParameter("id", high.PackageId));
        Assert.Equal(high.Id, (await f.SendAsync(new GetSoftwareQuery(f.SoftwareId))).LatestAvailableFormalReleaseId);
        Assert.Equal($"/api/v1/packages/{high.PackageId:D}/content", (await f.SendAsync(new GetClientReleaseQuery(high.Id), access: lowInstallation.Proof, instance: lowInstallation.Id)).DownloadPath);
        await PublicationScenario.SqlAsync(f, "UPDATE pkg.replicas SET \"State\"='Missing' WHERE \"PackageId\"=@id", new NpgsqlParameter("id", high.PackageId));
        Assert.Equal(low.Id, (await f.SendAsync(new GetSoftwareQuery(f.SoftwareId))).LatestAvailableFormalReleaseId);
        await PublicationScenario.SqlAsync(f, "UPDATE pkg.replicas SET \"State\"='Healthy',\"CheckedAt\"=clock_timestamp() WHERE \"PackageId\"=@id", new NpgsqlParameter("id", high.PackageId));
        await f.SendAsync(new DisableReleaseCommand(Guid.NewGuid(), high.Id, high.Revision, "最新正式版停用"));
        Assert.Equal(low.Id, (await f.SendAsync(new GetSoftwareQuery(f.SoftwareId))).LatestAvailableFormalReleaseId);
        await f.SendAsync(new DisableReleaseCommand(Guid.NewGuid(), low.Id, low.Revision, "无可用正式版"));
        Assert.Null((await f.SendAsync(new GetClientContextQuery(), access: lowInstallation.Proof, instance: lowInstallation.Id)).LatestAvailableFormalReleaseId);
        Assert.Empty((await f.SendAsync(new ListClientReleasesQuery(new(f.SoftwareId)), access: lowInstallation.Proof, instance: lowInstallation.Id)).Items);
        Assert.Equal(2, (await f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, Channel: "Formal")))).Items.Count);
    }

    [Fact]
    public async Task LatestAndFormalPaginationRespectCurrentReadPermissionAndCandidateQueryBoundary()
    {
        await using var f = await PackageFixture.CreateAsync(); await PublicationScenario.GrantAsync(f);
        var one = await PublicationScenario.ReadyAsync(f); var first = await PublicationScenario.InstallAsync(f, one);
        await f.SendAsync(PublicationScenario.Publish(one, first.Evidence));
        var two = await PublicationScenario.ReadyAsync(f); var second = await PublicationScenario.InstallAsync(f, two);
        await f.SendAsync(PublicationScenario.Publish(two, second.Evidence));
        var hidden = (await f.Instances.Site.SoftwareAsync("HIDDEN-SOFTWARE")).Value;
        await f.Instances.Site.SendAsync(new CreateBindingCommand(Guid.NewGuid(), f.Instances.Device.Id, hidden.Id, "分页隐藏软件"));
        var reader = await f.Instances.Site.PersonAsync([new(null, "asset.read"), new(f.SoftwareId, "software.read"), new(f.SoftwareId, "instance.read")]);
        await PublicationScenario.SqlAsync(f, "UPDATE pkg.replicas SET \"CheckedAt\"=clock_timestamp()");
        var catalog = await f.SendAsync(new ListSoftwareQuery(new(new(), PageSize: 1)), person: reader);
        Assert.Equal(f.SoftwareId, Assert.Single(catalog.Items).Id); Assert.Null(catalog.Next);
        Assert.Equal(two.Id, catalog.Items[0].LatestAvailableFormalReleaseId);
        var page = await f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, Channel: "Formal", PageSize: 1)), person: reader);
        Assert.Equal(two.Id, Assert.Single(page.Items).Id); Assert.NotNull(page.Next);
        Assert.Equal(one.Id, Assert.Single((await f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, Channel: "Formal", PageSize: 1, After: page.Next)), person: reader)).Items).Id);
        var administrator = await f.Instances.Site.PersonAsync([new(null, "identity.manage")]);
        var candidates = await f.SendAsync(new GetPermissionOptionsQuery(new(new(), 50)), person: administrator);
        Assert.Equal(2, candidates.Items.Count); Assert.All(candidates.Items, x => Assert.Null(x.LatestAvailableFormalReleaseId));
        await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(new GetReleaseQuery(two.Id), person: administrator));
        var user = await f.Instances.Site.UserAsync(reader.SubjectId);
        await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision, user.Permissions.Where(x => x.Operation != "software.read").ToArray(), "撤销软件查看"));
        await Rejected(RequestFailure.ResourceNotFound, () => f.SendAsync(new ListReleasesQuery(new(f.SoftwareId, Channel: "Formal", PageSize: 1, After: page.Next)), person: reader));
        Assert.Empty((await f.SendAsync(new ListSoftwareQuery(new(new(), 50)), person: reader)).Items);
        Assert.Empty((await f.SendAsync(new GetDeviceInventoryQuery(f.Instances.Device.Id, new(new(), 50)), person: reader)).Items);
        Assert.Null((await f.SendAsync(new GetInstanceQuery(first.Id), person: reader)).LatestAvailableFormalReleaseId);
    }
}
