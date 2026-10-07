using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class SiteCatalogTests
{
    [Fact]
    public async Task SoftwareAndOnlyThreeCreatorPermissionsCommitTogether()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var before = await f.UserAsync(f.Proof.SubjectId);
        var result = await f.SoftwareAsync(); var user = await f.UserAsync(f.Proof.SubjectId);
        Assert.Equal(new[] { "instance.manage", "instance.read", "software.read" }, user.Permissions.Where(p => p.SoftwareId == result.Value.Id).Select(p => p.Operation).Order());
        Assert.Equal(before.Revision + 1, user.Revision); Assert.Null(result.Value.LatestAvailableFormalReleaseId);
        Assert.Equal(1, await f.CountAsync("rel.software")); Assert.Equal(1, await f.CountAsync("rel.operation_results"));
        var page = await f.SendAsync(new ListSoftwareQuery(new(new(), 50))); Assert.Equal(result.Value, Assert.Single(page.Items));
    }
    [Theory] [InlineData("audit")] [InlineData("save")] [InlineData("cancel")] [InlineData("grant")]
    public async Task FailedSoftwareCreationRollsBackSoftwareGrantsSiteAuditAndResult(string stage)
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var user = await f.UserAsync(f.Proof.SubjectId); var audit = await f.CountAsync("aud.events");
        using var cancellation = new CancellationTokenSource(); IInterceptor? interceptor = stage == "save" ? new SaveFailure() : stage == "cancel" ? new SaveCancellation(cancellation) : null;
        await Assert.ThrowsAnyAsync<Exception>(() => f.SendAsync(new CreateSoftwareCommand(Guid.NewGuid(), "ROLLBACK", "夹具", "Vision", null),
            interceptor: interceptor, auditFailure: stage == "audit", grantFailure: stage == "grant", token: cancellation.Token));
        Assert.Equal(0, await f.CountAsync("rel.software")); Assert.Equal(0, await f.CountAsync("ins.site_identity")); Assert.Equal(0, await f.CountAsync("rel.operation_results"));
        Assert.Equal(audit, await f.CountAsync("aud.events"));
        var actual = await f.UserAsync(f.Proof.SubjectId);
        Assert.Equal(user.Revision, actual.Revision); Assert.Equal(user.Permissions, actual.Permissions);
        Assert.Equal(user.DisplayName, actual.DisplayName); Assert.Equal(user.IsEnabled, actual.IsEnabled);
    }
    [Fact]
    public async Task LostCommitConfirmationRestoresWithoutAnotherCreationOrGrant()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var loss = new LostConfirmation();
        var command = new CreateSoftwareCommand(Guid.NewGuid(), "LOST", "夹具", "Vision", null);
        var result = await f.SendAsync(command, interceptor: loss); Assert.Equal(1, loss.Commits);
        Assert.Equal(result.Value.Id, (await f.SendAsync(command)).Value.Id); Assert.Equal(1, await f.CountAsync("rel.software"));
        Assert.Equal(3, (await f.UserAsync(f.Proof.SubjectId)).Permissions.Count(p => p.SoftwareId is not null));
    }
    [Fact]
    public async Task ManufacturingHierarchyAndMultiSoftwareBindingsAreRealAndUnregistered()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var (process, device) = await f.DeviceAsync();
        var a = (await f.SoftwareAsync("A")).Value; var b = (await f.SoftwareAsync("B", "Vision")).Value;
        await f.SendAsync(new CreateBindingCommand(Guid.NewGuid(), device.Id, a.Id, "夹具映射")); await f.SendAsync(new CreateBindingCommand(Guid.NewGuid(), device.Id, b.Id, "夹具映射"));
        var inventory = await f.SendAsync(new GetDeviceInventoryQuery(device.Id, new(new(), 50)));
        Assert.Equal(new[] { "A", "B" }, inventory.Items.Select(x => x.Software.Code)); Assert.All(inventory.Items, x => Assert.Null(x.Software.LatestAvailableFormalReleaseId));
        var view = await f.SendAsync(new GetDeviceQuery(device.Id)); Assert.Equal(process.Id, view.ProcessId); Assert.Equal("夹具二期设备", view.Name);
        var second = (await f.SendAsync(new CreateDeviceCommand(Guid.NewGuid(), process.Id, "FIXTURE-D2", "夹具设备"))).Value;
        await f.SendAsync(new CreateBindingCommand(Guid.NewGuid(), second.Id, a.Id, "共用软件目录")); Assert.Equal(2, await f.CountAsync("rel.software"));
    }
    [Fact]
    public async Task BindingRecreationKeepsIdentityAndRevisionAndOldReplayCannotChangeIt()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var (_, d) = await f.DeviceAsync(); var s = (await f.SoftwareAsync()).Value;
        var create = new CreateBindingCommand(Guid.NewGuid(), d.Id, s.Id, "夹具映射"); var first = (await f.SendAsync(create)).Value;
        var remove = new RevokeBindingCommand(Guid.NewGuid(), d.Id, s.Id, first.Revision, "夹具撤销"); Assert.False((await f.SendAsync(remove)).Value.IsActive);
        var next = (await f.SendAsync(create with { Key = Guid.NewGuid() })).Value;
        Assert.Equal(first.Id, next.Id); Assert.Equal(3, next.Revision); await f.SendAsync(remove); await f.SendAsync(create);
        var page = await f.SendAsync(new ListBindingsQuery(d.Id, new(new(), 50))); Assert.True(Assert.Single(page.Items).IsActive); Assert.Equal(3, page.Items[0].Revision);
        Assert.Equal(RequestFailure.RevisionConflict, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(remove with { Key = Guid.NewGuid() }))).Failure);
        Assert.Equal(1, await f.CountAsync("ins.device_software_bindings"));
    }
    [Fact]
    public async Task ConfirmedInstanceReferencePreventsRevocationAndInactiveReferencesAreRejected()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var (_, d) = await f.DeviceAsync(); var s = (await f.SoftwareAsync()).Value;
        var binding = (await f.SendAsync(new CreateBindingCommand(Guid.NewGuid(), d.Id, s.Id, "夹具映射"))).Value;
        await using var provider = f.Provider(); await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async token =>
        { await scope.ServiceProvider.GetRequiredService<ISoftwareCatalog>().ExistsAsync(s.Id, true, token); await scope.ServiceProvider.GetRequiredService<ISiteAssets>().ConfirmInstanceReferenceAsync(d.Id, s.Id, token); return true; }, default);
        Assert.Equal(RequestFailure.InvalidState, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new RevokeBindingCommand(Guid.NewGuid(), d.Id, s.Id, binding.Revision, "不允许")))).Failure);
        var s2 = (await f.SoftwareAsync("INACTIVE")).Value;
        var inactive = (await f.SendAsync(new CreateBindingCommand(Guid.NewGuid(), d.Id, s2.Id, "暂时关联"))).Value;
        await f.SendAsync(new RevokeBindingCommand(Guid.NewGuid(), d.Id, s2.Id, inactive.Revision, "撤销关联"));
        await using var newScope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<RequestRejectedException>(() => newScope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async token =>
        { await newScope.ServiceProvider.GetRequiredService<ISiteAssets>().ConfirmInstanceReferenceAsync(d.Id, s2.Id, token); return true; }, default));
    }
    [Fact]
    public async Task DeviceProcessMovesKeepDeviceIdentityAndReplayPrecedesOldRevision()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var (p, d) = await f.DeviceAsync();
        var second = (await f.SendAsync(new CreateProcessCommand(Guid.NewGuid(), "SECOND", "第二工序"))).Value;
        var move = new UpdateDeviceCommand(Guid.NewGuid(), d.Id, d.Revision, second.Id, "设备改名", "工序调整");
        var moved = (await f.SendAsync(move)).Value; Assert.Equal(d.Id, moved.Id); Assert.Equal(d.DeviceNo, moved.DeviceNo); Assert.Equal(second.Id, moved.ProcessId);
        Assert.Equal(moved.Id, (await f.SendAsync(move)).Value.Id);
        Assert.Empty((await f.SendAsync(new ListDevicesQuery(new(new(ProcessId: p.Id), 50)))).Items);
        var rename = new UpdateProcessCommand(Guid.NewGuid(), second.Id, second.Revision, "工序改名", "更正名称");
        await f.SendAsync(rename); Assert.Equal("工序改名", (await f.SendAsync(new GetDeviceQuery(d.Id))).ProcessName);
        Assert.Equal(RequestFailure.RevisionConflict, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(rename with { Key = Guid.NewGuid() }))).Failure);
    }
    [Fact]
    public async Task BindingConcurrencyAndAuditFailureDoNotLoseOrDuplicateMappings()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var (_, d) = await f.DeviceAsync(); var s = (await f.SoftwareAsync()).Value;
        var command = new CreateBindingCommand(Guid.NewGuid(), d.Id, s.Id, "建立映射"); var before = await f.CountAsync("aud.events");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.SendAsync(command, auditFailure: true));
        Assert.Equal(0, await f.CountAsync("ins.device_software_bindings")); Assert.Equal(before, await f.CountAsync("aud.events"));
        var results = await Task.WhenAll(f.SendAsync(command), f.SendAsync(command)); Assert.Equal(results[0].Value.Id, results[1].Value.Id);
        Assert.Equal(1, await f.CountAsync("ins.device_software_bindings"));
        async Task<bool> Revoke() { try { await f.SendAsync(new RevokeBindingCommand(Guid.NewGuid(), d.Id, s.Id, 1, "并发撤销")); return true; } catch (RequestRejectedException e) when (e.Failure == RequestFailure.RevisionConflict) { return false; } }
        Assert.Equal(1, (await Task.WhenAll(Revoke(), Revoke())).Count(x => x));
    }
    [Fact]
    public async Task AssetAndSoftwarePermissionsAreIndependentAndInventoryIsFilteredBeforePaging()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var (_, d) = await f.DeviceAsync();
        var a = (await f.SoftwareAsync("A")).Value; var b = (await f.SoftwareAsync("B", "Vision")).Value;
        foreach (var s in new[] { a, b }) await f.SendAsync(new CreateBindingCommand(Guid.NewGuid(), d.Id, s.Id, "夹具映射"));
        var proof = await f.PersonAsync([new(null, "asset.read"), new(a.Id, "software.read")]);
        Assert.Single((await f.SendAsync(new ListSoftwareQuery(new(new(), 1)), proof)).Items);
        Assert.Single((await f.SendAsync(new ListBindingsQuery(d.Id, new(new(), 1)), proof)).Items);
        Assert.Empty((await f.SendAsync(new GetDeviceInventoryQuery(d.Id, new(new(), 1)), proof)).Items);
        Assert.Equal(RequestFailure.ResourceNotFound, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new GetSoftwareQuery(b.Id), proof))).Failure);
        var u = await f.UserAsync(proof.SubjectId); await f.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), u.Id, u.Revision,
            [new(null, "asset.read"), new(a.Id, "software.read"), new(a.Id, "instance.read")], "增加实例查看"));
        var inventory = await f.SendAsync(new GetDeviceInventoryQuery(d.Id, new(new(), 1)), proof); Assert.Single(inventory.Items); Assert.Null(inventory.Next);
        Assert.Equal(RequestFailure.PermissionDenied, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new CreateBindingCommand(Guid.NewGuid(), d.Id, a.Id, "越权"), proof))).Failure);
        Assert.Equal(RequestFailure.PermissionDenied, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new GetPermissionOptionsQuery(new(new(), 1)), proof))).Failure);
    }
    [Fact]
    public async Task GrantsValidateSoftwareAndCurrentRevocationBlocksACommittedReplay()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var (_, d) = await f.DeviceAsync(); var s = (await f.SoftwareAsync()).Value;
        var command = new CreateBindingCommand(Guid.NewGuid(), d.Id, s.Id, "夹具映射"); await f.SendAsync(command);
        var u = await f.UserAsync(f.Proof.SubjectId);
        Assert.Equal(RequestFailure.ResourceNotFound, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), u.Id, u.Revision,
            u.Permissions.Append(new(Guid.NewGuid(), "software.read")).ToArray(), "非法软件")))).Failure);
        Assert.Equal(RequestFailure.ValidationFailed, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), u.Id, u.Revision,
            u.Permissions.Append(new(null, "software.read")).ToArray(), "非法范围")))).Failure);
        await f.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), u.Id, u.Revision, u.Permissions.Where(p => p.SoftwareId != s.Id || p.Operation != "instance.manage").ToArray(), "撤销映射维护"));
        Assert.Equal(RequestFailure.ResourceNotFound, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(command))).Failure); Assert.Equal(1, await f.CountAsync("ins.device_software_bindings"));
    }
    [Fact]
    public async Task MissingConfigurationAndChangedSiteDoNotCreateOrSwitchLedgers()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(configured: false);
        Assert.Equal(RequestFailure.ConfigurationInvalid, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SoftwareAsync())).Failure);
        Assert.Equal(0, await f.CountAsync("rel.software"));
        await using var good = await SiteCatalogFixture.CreateAsync(); await good.DeviceAsync();
        Assert.Equal(RequestFailure.ConfigurationInvalid, (await Assert.ThrowsAsync<RequestRejectedException>(() => good.SendAsync(new ListProcessesQuery(new(new(), 50)),
            options: new(Guid.NewGuid(), "另一夹具厂区", "Asia/Shanghai")))).Failure);
    }
    [Fact]
    public async Task NaturalKeysAndConcurrentRevisionUpdatesHaveOneWinner()
    {
        await using var f = await SiteCatalogFixture.CreateAsync();
        async Task<bool> Create() { try { await f.SoftwareAsync("CONCURRENT"); return true; } catch (RequestRejectedException e) when (e.Failure == RequestFailure.InvalidState) { return false; } }
        Assert.Equal(1, (await Task.WhenAll(Create(), Create())).Count(x => x));
        var (p, d) = await f.DeviceAsync();
        Assert.Equal(RequestFailure.InvalidState, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new CreateProcessCommand(Guid.NewGuid(), p.Code, "重复")))).Failure);
        Assert.Equal(RequestFailure.InvalidState, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new CreateDeviceCommand(Guid.NewGuid(), p.Id, d.DeviceNo, "重复")))).Failure);
        Assert.Equal(RequestFailure.ResourceNotFound, (await Assert.ThrowsAsync<RequestRejectedException>(() => f.SendAsync(new CreateDeviceCommand(Guid.NewGuid(), Guid.NewGuid(), "BAD", "无工序")))).Failure);
        async Task<bool> Update(string name) { try { await f.SendAsync(new UpdateDeviceCommand(Guid.NewGuid(), d.Id, d.Revision, null, name, "并发资料")); return true; } catch (RequestRejectedException e) when (e.Failure == RequestFailure.RevisionConflict) { return false; } }
        Assert.Equal(1, (await Task.WhenAll(Update("甲"), Update("乙"))).Count(x => x));
    }
    [Fact]
    public async Task ReadOnlyQueriesUseStableEscapedPaginationAndAdminOptionsDoNotNeedBusinessAccess()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); await f.SoftwareAsync("A_2"); await f.SoftwareAsync("A_1"); await f.SoftwareAsync("AB3");
        var first = await f.SendAsync(new ListSoftwareQuery(new(new(Code: "A_"), 1))); Assert.Equal("A_1", Assert.Single(first.Items).Code); Assert.NotNull(first.Next);
        var next = await f.SendAsync(new ListSoftwareQuery(new(new(Code: "A_"), 1, first.Next))); Assert.Equal("A_2", Assert.Single(next.Items).Code); Assert.Null(next.Next);
        var proof = await f.PersonAsync([new(null, "identity.manage")]);
        Assert.Empty((await f.SendAsync(new ListSoftwareQuery(new(new(), 50)), proof)).Items);
        var options = await f.SendAsync(new GetPermissionOptionsQuery(new(new(), 50)), proof); Assert.Equal(3, options.Items.Count); Assert.All(options.Items, s => Assert.Null(s.Description));
        Assert.Equal(13, options.SoftwareOperations.Count);
    }
    private sealed class SaveFailure : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken token = default) => throw new InvalidOperationException("fixture save failure"); }
    private sealed class SaveCancellation(CancellationTokenSource cancellation) : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken token = default) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.FromResult(result); } }
    private sealed class LostConfirmation : DbTransactionInterceptor
    { internal int Commits; public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken token = default) { Commits++; throw new IOException("fixture lost commit confirmation"); } }
}
