using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Plc;
using Xunit;
using static Svm.FrameworkTests.InstanceAccessTests;
namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class PlcArchiveTests
{
    [Fact] public async Task ControllerMainLibraryIsUniqueAndSharedLibraryKeepsIndependentConfirmation()
    {
        await using var f=await PlcFixture.CreateAsync(); var second=await f.Controller();
        var library=(await f.P.Instances.Site.SoftwareAsync("PLC-OTHER","PLC")).Value;
        await Rejected(RequestFailure.InvalidState,()=>f.Send(new CreateHardwareBindingCommand(Guid.NewGuid(),f.Position.HardwareId,library.Id,null,"第二主库")));
        var ipc=(await f.Send(new CreateHardwareCommand(Guid.NewGuid(),f.Position.DeviceId,"IPC","类型夹具",null,null))).Value;
        await Rejected(RequestFailure.InvalidState,()=>f.Send(new CreateHardwareBindingCommand(Guid.NewGuid(),ipc.Id,f.Software,null,"不合法类型")));
        var r=await f.Ready(); await f.Send(f.Use(r));
        var rows=await f.Send(new GetWorkspaceInstallationsQuery(new(new(SoftwareId:f.Software))));
        Assert.Equal(2,rows.TotalCount); Assert.Equal("Confirmed",rows.Items.Single(x=>x.Position!.Id==f.Position.Id).LocationState);
        Assert.Equal("PendingConfirmation",rows.Items.Single(x=>x.Position!.Id==second.Id).LocationState);
        Assert.Null(rows.Items.Single(x=>x.Position!.Id==second.Id).PlcUsage!.Current);
    }
    [Fact] public async Task HumanPublicationHasSeparateFieldEvidenceAndRequiresBothPermissions()
    {
        await using var f=await PlcFixture.CreateAsync(); var r=await f.Ready();
        var publisher=await f.P.Instances.Site.PersonAsync([new(f.Software,"release.publish")]);
        await Rejected(RequestFailure.ResourceNotFound,()=>f.Send(f.Publish(r),person:publisher));
        var cmd=f.Publish(r); var first=(await f.Send(cmd)).Value;
        Assert.Equal("Formal",first.State); Assert.NotNull(first.PlcEvidenceId); Assert.Null(first.TestEvidenceId);
        Assert.Equal(first,(await f.Send(cmd)).Value);
        Assert.Equal(0,await f.P.Count("ins.installation_evidence")); Assert.Equal(1,await f.P.Count("rel.plc_publications"));
        Assert.Null((await f.Send(new GetPlcUsageQuery(f.Position.Id))).Current);
        await Rejected(RequestFailure.RevisionConflict,()=>f.Send(cmd with {Key=Guid.NewGuid()}));
        await Rejected(RequestFailure.InvalidState,()=>f.Send(new PublishReleaseCommand(Guid.NewGuid(),r.Id,first.Revision,Guid.NewGuid(),"错误入口","错误入口")));
        var fieldReader=await f.P.Instances.Site.PersonAsync([new(null,"asset.read"),new(f.Software,"software.read")]);
        Assert.NotNull(await f.Send(new GetPlcEvidenceQuery(r.Id),person:fieldReader));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.Send(new GetPlcEvidenceQuery(r.Id),person:publisher));
    }
    [Fact] public async Task LateUsageCorrectionAndReassociationNeverRewriteHistoricalFacts()
    {
        await using var f=await PlcFixture.CreateAsync(); var a=await f.Ready(); var b=await f.Ready();
        var original=(await f.Send(f.Use(b))).Value;
        var older=f.Use(a,1) with {FieldAt=f.Now.AddHours(-2)}; await f.Send(older);
        Assert.Equal(b.Id,(await f.Send(new GetPlcUsageQuery(f.Position.Id))).Current!.ReleaseId);
        var correction=f.Use(a,2) with {CorrectsId=original.Id,FieldAt=original.FieldAt}; await f.Send(correction);
        Assert.Equal(a.Id,(await f.Send(new GetPlcUsageQuery(f.Position.Id))).Current!.ReleaseId);
        await f.Send(new DisableReleaseCommand(Guid.NewGuid(),b.Id,b.Revision,"现场旧归档"));
        await f.Send(f.Use(b,3) with {FieldAt=f.Now.AddHours(-3)});
        var history=await f.Send(new ListPlcUsageQuery(f.Position.HardwareId)); Assert.Equal(4,history.Items.Count);
        Assert.Contains(history.Items,x=>x.ReleaseId==b.Id&&x.ReleaseState=="Disabled");
        await f.Send(new RevokeHardwareBindingCommand(Guid.NewGuid(),f.Position.HardwareId,f.Software,f.Position.Revision,"拆除"));
        var active=(await f.Send(new CreateHardwareBindingCommand(Guid.NewGuid(),f.Position.HardwareId,f.Software,null,"重新关联"))).Value;
        Assert.Equal(f.Position.Id,active.Id); Assert.Null((await f.Send(new GetPlcUsageQuery(active.Id))).Current);
        Assert.Equal(4,(await f.Send(new ListPlcUsageQuery(active.HardwareId))).Items.Count);
    }
    [Fact] public async Task CredentialScopeOwnerExpiryRevocationAndAcceptedWorkAreIndependent()
    {
        await using var f=await PlcFixture.CreateAsync(); var (credential,proof)=await f.Credential();
        var cmd=f.Archive(); var result=(await f.Send(cmd,proof)).Value;
        Assert.Equal(result,(await f.Send(cmd,proof)).Value);
        var other=await f.Controller(); await Rejected(RequestFailure.PermissionDenied,()=>f.Send(cmd with {Key=Guid.NewGuid(),BindingId=other.Id,BindingRevision=other.Revision},proof));
        var (_,second)=await f.Credential(); await Rejected(RequestFailure.ResourceNotFound,()=>f.Send(new PlcGetPackageQuery(result.UploadId,true),second));
        var receipt=(await f.Send(new PlcBeginUploadCommand(result.UploadId,Guid.NewGuid(),"node-a"),proof)).Value;
        await f.Send(new PlcFinishUploadCommand(receipt,PackageFixture.Input.SizeBytes,PackageFixture.Input.Sha256),proof);
        await f.Accept(result.UploadId);
        await f.Send(new RevokePlcCredentialCommand(Guid.NewGuid(),credential.Id,credential.Revision,"吊销"));
        await Rejected(RequestFailure.CredentialInvalid,()=>f.Send(cmd,proof));
        var lease=(await f.P.SendAsync(new ClaimPackageWorkCommand(result.UploadId,"node-a",Guid.NewGuid()),work:result.UploadId)).Value!;
        await f.P.SendAsync(new CompletePackageWorkCommand(lease,f.P.Healthy),work:result.UploadId);
        Assert.Equal("Test",(await f.Send(new GetReleaseQuery(result.Release.Id))).State);
        f.P.Instances.Clock.Advance(TimeSpan.FromHours(2));
        await Rejected(RequestFailure.CredentialInvalid,()=>f.Send(f.Archive(),second));
        var saved=await PersistenceDatabase.ScalarAsync<string>(f.P.Database.MigrationConnection,"SELECT string_agg(\"SecretHash\",',') FROM iam.plc_upload_credentials"); Assert.DoesNotContain(proof.Secret,saved);
    }
    [Fact] public async Task ChangedBindingCannotRestoreOldCredentialScopeAndConcurrentVersionsAreUnique()
    {
        await using var f=await PlcFixture.CreateAsync(); var (_,proof)=await f.Credential();
        var results=await Task.WhenAll(f.Send(f.Archive(),proof),f.Send(f.Archive(),proof));
        Assert.Equal(new[]{"1.0.0","1.0.1"},results.Select(x=>x.Value.Release.Version).Order().ToArray());
        await f.Send(new RevokeHardwareBindingCommand(Guid.NewGuid(),f.Position.HardwareId,f.Software,f.Position.Revision,"解除"));
        await f.Send(new CreateHardwareBindingCommand(Guid.NewGuid(),f.Position.HardwareId,f.Software,null,"重建"));
        await Rejected(RequestFailure.RevisionConflict,()=>f.Send(f.Archive(),proof));
    }
    [Fact] public async Task AuditFailureRollsBackPublicationAndUsageAndCommitLossOnlyVerifies()
    {
        await using var f=await PlcFixture.CreateAsync(); var r=await f.Ready(); var audit=await f.P.Count("aud.events");
        await Assert.ThrowsAsync<PersistenceException>(()=>f.Send(f.Publish(r),auditFailure:true));
        Assert.Equal("Test",(await f.Send(new GetReleaseQuery(r.Id))).State); Assert.Equal(0,await f.P.Count("rel.plc_publications"));
        await Assert.ThrowsAsync<PersistenceException>(()=>f.Send(f.Use(r),auditFailure:true));
        Assert.Equal(0,await f.P.Count("ins.plc_usage_records")); Assert.Equal(audit,await f.P.Count("aud.events"));
        var command=f.Publish(r); var loss=new LostConfirmation(); var result=await f.Send(command,interceptor:loss);
        Assert.Equal(1,loss.Commits); Assert.Equal(result,await f.Send(command)); Assert.Equal(1,await f.P.Count("rel.plc_publications"));
    }
    [Fact] public async Task ConcurrentUsagePreservesRevisionAndProductReadDoesNotRevealControllerHistory()
    {
        await using var f=await PlcFixture.CreateAsync(); var r=await f.Ready();
        async Task<Exception?> Attempt()=>await Record.ExceptionAsync(()=>f.Send(f.Use(r)));
        var outcomes=await Task.WhenAll(Attempt(),Attempt());
        Assert.Single(outcomes,x=>x is null);
        Assert.Equal(RequestFailure.RevisionConflict,Assert.IsType<RequestRejectedException>(Assert.Single(outcomes,x=>x is not null)).Failure);
        Assert.Equal(1,await f.P.Count("ins.plc_usage_records"));
        var reader=await f.P.Instances.Site.PersonAsync([new(f.Software,"software.read")]);
        await Rejected(RequestFailure.ResourceNotFound,()=>f.Send(new GetPlcUsageQuery(f.Position.Id),person:reader));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.Send(new ListPlcUsageQuery(f.Position.HardwareId),person:reader));
        Assert.Equal(r.Id,(await f.Send(new GetReleaseQuery(r.Id),person:reader)).Id);
    }
    [Fact] public async Task StaleReplicasAndOnlineEnrollmentAreRejectedForPlc()
    {
        await using var f=await PlcFixture.CreateAsync(); var r=await f.Ready();
        f.P.Instances.Clock.Advance(TimeSpan.FromSeconds(PackageFixture.Execution.ReplicaCheckSeconds*2));
        await Rejected(RequestFailure.InvalidState,()=>f.Send(f.Publish(r)));
        Assert.Equal("Test",(await f.Send(new GetReleaseQuery(r.Id))).State);
        Assert.Equal(0,await f.P.Count("rel.plc_publications"));
        await Rejected(RequestFailure.InvalidState,()=>f.Send(new CreateEnrollmentGrantCommand(Guid.NewGuid(),f.Software,
            [f.Position.DeviceId],f.Now.AddHours(1),1,InstanceFixture.Secret(),"拒绝 PLC 在线登记",f.Position.Id,f.Position.Revision)));
        await Rejected(RequestFailure.InvalidState,()=>f.Send(new Svm.Services.Contracts.Releases.GetReleaseSourceQuery(f.Software)));
    }
}
internal sealed class PlcFixture(PackageFixture package,Guid software,HardwareBindingView position):IAsyncDisposable
{
    internal PackageFixture P=>package; internal Guid Software=>software; internal HardwareBindingView Position=>position;
    internal DateTimeOffset Now=>P.Instances.Clock.GetUtcNow();
    internal static async Task<PlcFixture> CreateAsync()
    {
        var p=await PackageFixture.CreateAsync(); try{
        var s=(await p.Instances.Site.SoftwareAsync("PLC-LIBRARY","PLC")).Value;
        var u=await p.Instances.Site.UserAsync(p.Instances.Site.Proof.SubjectId);
        await p.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),u.Id,u.Revision,u.Permissions.Concat(new[]{"release.upload","release.publish","release.disable","plc.verify","plc.usage.record","plc.uploadCredential.manage","enrollment.manage","audit.read"}.Select(o=>new PermissionView(s.Id,o))).ToArray(),"PLC 夹具显式授权"));
        var h=(await p.SendAsync(new CreateHardwareCommand(Guid.NewGuid(),p.Instances.Device.Id,"PLC","虚构主控",null,null))).Value;
        var b=(await p.SendAsync(new CreateHardwareBindingCommand(Guid.NewGuid(),h.Id,s.Id,null,"PLC 夹具"))).Value; return new(p,s.Id,b);
        }catch{await p.DisposeAsync();throw;}
    }
    internal async Task<T> Send<T>(IRequest<T> x,AccessProof? proof=null,SessionProof? person=null,bool auditFailure=false,IInterceptor? interceptor=null)
    {await using var p=P.Provider(person:person,access:proof,plcSoftware:Software,auditFailure:auditFailure,interceptor:interceptor); await using var scope=p.CreateAsyncScope();return await scope.ServiceProvider.GetRequiredService<ISender>().Send(x);}
    internal async Task<HardwareBindingView> Controller(){var h=(await Send(new CreateHardwareCommand(Guid.NewGuid(),Position.DeviceId,"PLC","虚构共享控制器",null,null))).Value;return (await Send(new CreateHardwareBindingCommand(Guid.NewGuid(),h.Id,Software,null,"共享主库"))).Value;}
    internal async Task<(PlcCredentialView,AccessProof)> Credential(){var secret=InstanceFixture.Secret();var c=(await Send(new CreatePlcCredentialCommand(Guid.NewGuid(),Software,"工具夹具",[new(Position.Id,Position.Revision)],Now.AddHours(1),secret))).Value;return (c,new(ActorKind.PlcUpload,c.Id,secret));}
    internal CreatePlcArchiveCommand Archive()=>new(Guid.NewGuid(),Position.Id,Position.Revision,"Patch","虚构归档","夹具",null,PackageFixture.Input);
    internal PublishPlcReleaseCommand Publish(ReleaseView r)=>new(Guid.NewGuid(),r.Id,r.Revision,Position.Id,Position.Revision,Now.AddHours(-1),"虚构现场验证","夹具材料说明","夹具发布","通过");
    internal RecordPlcUsageCommand Use(ReleaseView r,long revision=0)=>new(Guid.NewGuid(),Position.Id,Position.Revision,r.Id,revision,Now.AddHours(-1),"虚构现场使用","夹具材料");
    internal async Task<ReleaseView> Ready(){var r=(await Send(new CreateReleaseCommand(Guid.NewGuid(),Software,"Patch","夹具","归档",null,PackageFixture.Input))).Value;var lease=await P.Lease(r);await P.SendAsync(new CompletePackageWorkCommand(lease,P.Healthy),work:r.UploadId);return await Send(new GetReleaseQuery(r.Release.Id));}
    internal async Task Accept(Guid id){await using var p=P.Provider();await using var scope=p.CreateAsyncScope();var pkg=scope.ServiceProvider.GetRequiredService<IPackages>();var a=await pkg.AuthorityAsync(id,false,default);await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(),async ct=>{await pkg.AcceptAsync(id,a.DispatchSequence,a.DispatchEventId,ct);return true;},default);}
    public ValueTask DisposeAsync()=>P.DisposeAsync();
}
