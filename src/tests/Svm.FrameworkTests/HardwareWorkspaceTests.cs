using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class HardwareWorkspaceTests
{
    [Fact]
    public async Task AutomaticCodesRemainUniqueDuringConcurrentCreationAndSkipHistoricalCodes()
    {
        await using var f = await SiteCatalogFixture.CreateAsync();
        var old = (await f.SendAsync(new CreateProcessCommand(Guid.NewGuid(), "PR-000001", "历史编号夹具"))).Value;
        var processes = await Task.WhenAll(Enumerable.Range(0, 4).Select(n => f.SendAsync(new CreateProcessCommand(Guid.NewGuid(), null, "自动编号夹具" + n))));
        Assert.Equal(4, processes.Select(x => x.Value.Code).Distinct().Count()); Assert.All(processes, x => Assert.NotEqual(old.Code, x.Value.Code));
        var devices = await Task.WhenAll(Enumerable.Range(0, 4).Select(n => f.SendAsync(new CreateDeviceCommand(Guid.NewGuid(), old.Id, null, "并发设备" + n))));
        Assert.Equal(4, devices.Select(x => x.Value.DeviceNo).Distinct().Count()); Assert.All(devices, x => Assert.StartsWith("EQ-", x.Value.DeviceNo));
        var products = await Task.WhenAll(Enumerable.Range(0, 3).Select(n => f.SendAsync(new CreateSoftwareCommand(Guid.NewGuid(), null, "并发软件" + n, "Vision", null))));
        Assert.Equal(3, products.Select(x => x.Value.Code).Distinct().Count()); Assert.All(products, x => Assert.StartsWith("SW-", x.Value.Code));
        Assert.Equal(old, await f.SendAsync(new GetProcessQuery(old.Id)));
    }
    [Fact]
    public async Task HardwareSupportsManyToManyAndCountsUseTheSamePermissionFilterAsPages()
    {
        await using var f = await InstanceFixture.CreateAsync(); var other = (await f.Site.SoftwareAsync("VISIBLE-SECOND", "Vision")).Value;
        var a = await f.PositionAsync(); var b = await f.PositionAsync();
        await f.Site.SendAsync(new CreateHardwareBindingCommand(Guid.NewGuid(), a.HardwareId, other.Id, null, "同硬件第二软件"));
        var input = new HardwareListInput(new(DeviceId: f.Device.Id), 1); var rows = new List<WorkspaceInstallationView>();
        do { var page = await f.SendAsync(new GetWorkspaceInstallationsQuery(input)); Assert.Equal(3, page.TotalCount); rows.AddRange(page.Items); input = input with { After = page.Next }; } while (input.After is not null);
        Assert.Equal(3, rows.Select(x => x.RowId).Distinct().Count()); Assert.All(rows, x => { Assert.NotNull(x.Hardware); Assert.Null(x.Instance); Assert.Equal("NotRegistered", x.LocationState); });
        var devices = await f.SendAsync(new GetWorkspaceDevicesQuery(new(new(), 1))); var device = Assert.Single(devices.Items);
        Assert.Equal(2, device.HardwareCount); Assert.Equal(3, device.InstallationCount);
        var limited = await f.Site.PersonAsync([new(null, "asset.read"), new(f.SoftwareId, "software.read"), new(f.SoftwareId, "instance.read")]);
        var filtered = await f.SendAsync(new GetWorkspaceInstallationsQuery(new(new(), 200)), human: limited);
        Assert.Equal(2, filtered.TotalCount); Assert.All(filtered.Items, x => Assert.Equal(f.SoftwareId, x.Software.Id));
        var totals = await f.SendAsync(new GetWorkspaceDevicesQuery(new(new(), 50)), human: limited); Assert.Equal(2, Assert.Single(totals.Items).InstallationCount);
        var user = await f.Site.UserAsync(limited.SubjectId);
        await f.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision, [new(null,"asset.read"),new(f.SoftwareId,"software.read")], "撤销实例查看"));
        Assert.Equal(0, (await f.SendAsync(new GetWorkspaceInstallationsQuery(new(new(), 50)), human: limited)).TotalCount);
        Assert.Equal(0, Assert.Single((await f.SendAsync(new GetWorkspaceDevicesQuery(new(new(), 50)), human: limited)).Items).InstallationCount);
    }
    [Fact]
    public async Task NewSoftwareBindingAndCreatorGrantsCommitTogetherOrRollbackTogether()
    {
        await using var f = await SiteCatalogFixture.CreateAsync(); var d = (await f.DeviceAsync()).Device;
        var h = (await f.SendAsync(new CreateHardwareCommand(Guid.NewGuid(), d.Id, "IPC", "组合创建夹具", null, "192.0.2.22"))).Value;
        var x = new CreateHardwareBindingCommand(Guid.NewGuid(), h.Id, null, new("组合软件", "Vision", null), "硬件内创建软件");
        var before = await f.CountAsync("rel.software"); var audits = await f.CountAsync("aud.events"); var operations = await f.CountAsync("ins.operation_results");
        await Assert.ThrowsAnyAsync<Exception>(() => f.SendAsync(x, grantFailure: true));
        Assert.Equal(before, await f.CountAsync("rel.software")); Assert.Equal(audits, await f.CountAsync("aud.events")); Assert.Equal(operations, await f.CountAsync("ins.operation_results"));
        var first = await f.SendAsync(x); var replay = await f.SendAsync(x); Assert.Equal(first.Value, replay.Value);
        Assert.Equal(before + 1, await f.CountAsync("rel.software")); Assert.Equal(audits + 1, await f.CountAsync("aud.events"));
        var permissions = (await f.UserAsync(f.Proof.SubjectId)).Permissions.Where(p => p.SoftwareId == first.Value.SoftwareId).Select(p => p.Operation).Order().ToArray();
        Assert.Equal(new[] { "instance.manage", "instance.read", "software.read" }, permissions);
        var plc = (await f.SendAsync(new CreateHardwareCommand(Guid.NewGuid(), d.Id, "PLC", "PLC 仅台账", null, null))).Value;
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState, () => f.SendAsync(x with { Key = Guid.NewGuid(), HardwareId = plc.Id }));
        Assert.Equal(before + 1, await f.CountAsync("rel.software"));
    }
    [Fact]
    public async Task BindingReactivationRetainsIdentityAndOldKeysCannotChangeItAgain()
    {
        await using var f = await InstanceFixture.CreateAsync(); var position = await f.PositionAsync();
        var remove = new RevokeHardwareBindingCommand(Guid.NewGuid(), position.HardwareId, position.SoftwareId, position.Revision, "夹具撤销");
        var revoked = (await f.SendAsync(remove)).Value;
        var create = new CreateHardwareBindingCommand(Guid.NewGuid(), position.HardwareId, position.SoftwareId, null, "夹具重新建立");
        var active = (await f.SendAsync(create)).Value; Assert.Equal(position.Id, active.Id); Assert.True(active.Revision > revoked.Revision);
        Assert.True((await f.SendAsync(remove)).Value.IsActive); Assert.Equal(active, (await f.SendAsync(create)).Value);
        await InstanceAccessTests.Rejected(RequestFailure.RevisionConflict, () => f.SendAsync(remove with { Key = Guid.NewGuid() }));
        var parent = Assert.Single((await f.SendAsync(new ListBindingsQuery(f.Device.Id, new(new(), 50)))).Items);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState, () => f.SendAsync(new RevokeBindingCommand(Guid.NewGuid(), f.Device.Id, f.SoftwareId, parent.Revision, "不得绕过子位置")));
    }
    [Fact]
    public async Task TwoGrantsForOnePositionCannotCreateTwoInstancesAndStalePositionGrantsFail()
    {
        await using var f = await InstanceFixture.CreateAsync(); var position = await f.PositionAsync();
        async Task<AccessProof> Grant()
        {
            var secret = InstanceFixture.Secret(); var g = (await f.SendAsync(new CreateEnrollmentGrantCommand(Guid.NewGuid(),f.SoftwareId,[f.Device.Id],f.Clock.GetUtcNow().AddHours(1),1,secret,"位置并发许可",position.Id,position.Revision))).Value;
            return new(ActorKind.EnrollmentGrant, g.Id, secret);
        }
        var grants = new[] { await Grant(), await Grant() };
        async Task<bool> Enroll(AccessProof proof)
        { try { await f.SendAsync(new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,InstanceFixture.Secret()),proof); return true; } catch (RequestRejectedException e) when (e.Failure is RequestFailure.InvalidState or RequestFailure.RevisionConflict) { return false; } }
        Assert.Equal(1, (await Task.WhenAll(grants.Select(Enroll))).Count(x => x)); Assert.Equal(1, await f.CountAsync("ins.instances"));
        var current = Assert.Single((await f.SendAsync(new ListHardwareBindingsQuery(position.HardwareId,new(new())))).Items);
        Assert.NotNull(current.InstanceId);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState, () => f.SendAsync(new RevokeHardwareBindingCommand(Guid.NewGuid(),position.HardwareId,f.SoftwareId,current.Revision,"已引用位置")));
        var vacant = await f.PositionAsync(); var secret2 = InstanceFixture.Secret(); var g2=(await f.SendAsync(new CreateEnrollmentGrantCommand(Guid.NewGuid(),f.SoftwareId,[f.Device.Id],f.Clock.GetUtcNow().AddHours(1),1,secret2,"随后撤销位置",vacant.Id,vacant.Revision))).Value;
        await f.SendAsync(new RevokeHardwareBindingCommand(Guid.NewGuid(),vacant.HardwareId,f.SoftwareId,vacant.Revision,"撤销"));
        await f.SendAsync(new CreateHardwareBindingCommand(Guid.NewGuid(),vacant.HardwareId,f.SoftwareId,null,"恢复空位置"));
        await InstanceAccessTests.Rejected(RequestFailure.RevisionConflict, () => f.SendAsync(new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,InstanceFixture.Secret()),new AccessProof(ActorKind.EnrollmentGrant,g2.Id,secret2)));
    }
    [Fact]
    public async Task LegacyInstanceKeepsReportsAndRecoveryWhileOnlyOneExplicitHardwareAssignmentIsAllowed()
    {
        await using var f = await InstanceFixture.CreateAsync(); var e = await f.EnrollAsync(); var id=e.Registration.InstanceId;
        // Simulate the nullable historical columns produced by upgrading an existing installation.
        await PersistenceDatabase.ExecuteAsync(f.Site.Personnel.Personnel.Database.MigrationConnection,$"""
            UPDATE ins.instances SET "HardwareBindingId"=NULL WHERE "Id"='{id}';
            UPDATE ins.hardware_software_bindings SET "InstanceId"=NULL WHERE "InstanceId"='{id}';
            UPDATE iam.enrollment_grants SET "HardwareBindingId"=NULL,"HardwareBindingRevision"=NULL WHERE "Id"='{e.GrantProof.Id}';
            """);
        Assert.Equal(e.Registration,(await f.SendAsync(e.Command,e.GrantProof)).Value);
        await InstanceAccessTests.Rejected(RequestFailure.InvalidState,()=>f.SendAsync(e.Command with { Key=Guid.NewGuid(),InstallationKey=Guid.NewGuid() },e.GrantProof));
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),0),e.Proof,id); await f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report()),e.Proof,id);
        var old=await f.SendAsync(new GetInstanceQuery(id)); Assert.Null(old.HardwareBindingId); Assert.Equal("Fresh",old.Freshness);
        Assert.Contains((await f.SendAsync(new GetWorkspaceInstallationsQuery(new(new())))).Items,x=>x.Instance?.Id==id && x.LocationState=="PendingHardware");
        var position=await f.PositionAsync(); var command=new BindInstanceHardwareCommand(Guid.NewGuid(),id,position.Id,old.Revision,position.Revision,"人员核实工控机归属");
        var bound=(await f.SendAsync(command)).Value; Assert.Equal(position.Id,bound.HardwareBindingId); Assert.Equal(bound,(await f.SendAsync(command)).Value);
        await InstanceAccessTests.Rejected(RequestFailure.RevisionConflict,()=>f.SendAsync(command with { Key=Guid.NewGuid() }));
        var report=await f.SendAsync(new GetInstanceQuery(id)); Assert.Equal(System.Text.Json.JsonSerializer.Serialize(old.LastSnapshot),System.Text.Json.JsonSerializer.Serialize(report.LastSnapshot)); Assert.Equal(old.LastAcceptedAt,report.LastAcceptedAt);
        Assert.Single((await f.SendAsync(new GetInstanceHistoryQuery(id))).Items);
        var secret=InstanceFixture.Secret(); var recovery=(await f.SendAsync(new CreateRecoveryGrantCommand(Guid.NewGuid(),id,f.Clock.GetUtcNow().AddHours(1),secret,"归属补齐后恢复"))).Value;
        var result=(await f.SendAsync(new RecoverInstanceCommand(Guid.NewGuid(),InstanceFixture.Secret()),new AccessProof(ActorKind.RecoveryGrant,recovery.Id,secret),id)).Value;
        Assert.Equal(id,result.InstanceId); Assert.Equal(position.Id,(await f.SendAsync(new GetInstanceQuery(id))).HardwareBindingId);
    }
}
