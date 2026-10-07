using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class InstanceAccessTests
{
    [Fact]
    public async Task RegistrationQuotaIdentityMappingAuditAndNaturalResultCommitTogether()
    {
        await using var f=await InstanceFixture.CreateAsync(); var g=await f.GrantAsync(1); var audit=await f.CountAsync("aud.events"); var operations=await f.CountAsync("iam.operation_results");
        var c=new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,InstanceFixture.Secret());
        var values=await Task.WhenAll(f.SendAsync(c,g.Proof),f.SendAsync(c,g.Proof)); Assert.Equal(values[0].Value,values[1].Value);
        Assert.Equal(1,await f.CountAsync("ins.instances")); Assert.Equal(1,await f.CountAsync("iam.instance_subjects")); Assert.Equal(1,await f.CountAsync("iam.instance_credentials")); Assert.Equal(1,await f.CountAsync("iam.registrations")); Assert.Equal(audit+1,await f.CountAsync("aud.events"));
        var row=Assert.Single((await f.SendAsync(new ListEnrollmentGrantsQuery(f.SoftwareId))).Items); Assert.Equal(1,row.UsedCount); Assert.Equal("Exhausted",row.State);
        Assert.Equal(operations,await f.CountAsync("iam.operation_results")); // Registration uses its own durable natural key.
        var result=values[0].Value; var view=await f.SendAsync(new GetInstanceQuery(result.InstanceId)); Assert.Equal("NeverReported",view.Freshness); Assert.Null(view.LastSnapshot); Assert.Null(view.LastAcceptedAt);
        var inventory=await f.SendAsync(new GetDeviceInventoryQuery(f.Device.Id,new(new(),50))); Assert.Equal(view,Assert.Single(inventory.Items).Instance);
        await Rejected(RequestFailure.GrantExhausted,()=>f.SendAsync(c with { Key=Guid.NewGuid(),InstallationKey=Guid.NewGuid() },g.Proof));
    }
    [Theory][InlineData("Expired")][InlineData("Revoked")][InlineData("Exhausted")]
    public async Task InactiveGrantAllowsOnlyOriginalResultWithStillValidOriginalSecret(string state)
    {
        await using var f=await InstanceFixture.CreateAsync(); var g=await f.GrantAsync(1); var c=new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,InstanceFixture.Secret()); var r=(await f.SendAsync(c,g.Proof)).Value;
        if(state=="Expired") f.Clock.Advance(TimeSpan.FromHours(2));
        if(state=="Revoked") { var current=Assert.Single((await f.SendAsync(new ListEnrollmentGrantsQuery(f.SoftwareId))).Items); await f.SendAsync(new RevokeEnrollmentGrantCommand(Guid.NewGuid(),g.Grant.Id,current.Revision,"夹具撤销")); }
        Assert.Equal(r,(await f.SendAsync(c,g.Proof)).Value);
        var failure=state switch { "Expired"=>RequestFailure.GrantExpired,"Revoked"=>RequestFailure.GrantRevoked,_=>RequestFailure.GrantExhausted };
        await Rejected(failure,()=>f.SendAsync(c with { Key=Guid.NewGuid(),InstallationKey=Guid.NewGuid() },g.Proof));
        await Rejected(RequestFailure.RegistrationConflict,()=>f.SendAsync(c with { SecretMaterial=InstanceFixture.Secret() },g.Proof));
        var cred=Assert.Single((await f.SendAsync(new ListInstanceCredentialsQuery(r.InstanceId))).Items); await f.SendAsync(new RevokeInstanceCredentialCommand(Guid.NewGuid(),cred.Id,cred.Revision,"吊销原秘密"));
        await Rejected(RequestFailure.CredentialInvalid,()=>f.SendAsync(c,g.Proof)); Assert.Equal(1,await f.CountAsync("ins.instances"));
    }
    [Fact]
    public async Task ConcurrentInstallationsCannotExceedGrantCapacityOrTakeOverAnotherIdentity()
    {
        await using var f=await InstanceFixture.CreateAsync(); var g=await f.GrantAsync(1);
        async Task<bool> Create() { try { await f.SendAsync(new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,InstanceFixture.Secret()),g.Proof); return true; } catch(RequestRejectedException e) when(e.Failure==RequestFailure.GrantExhausted) { return false; } }
        Assert.Equal(1,(await Task.WhenAll(Create(),Create())).Count(x=>x)); Assert.Equal(1,await f.CountAsync("ins.instances"));
        var enrolled=await f.EnrollAsync(); var other=await f.GrantAsync();
        await Rejected(RequestFailure.RegistrationConflict,()=>f.SendAsync(enrolled.Command,other.Proof));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(enrolled.Command with { SoftwareId=Guid.NewGuid() },enrolled.GrantProof));
    }
    [Fact]
    public async Task MappingRevocationAndRegistrationHaveOneConsistentWinner()
    {
        await using var f=await InstanceFixture.CreateAsync(); var g=await f.GrantAsync(); var b=Assert.Single((await f.SendAsync(new ListBindingsQuery(f.Device.Id,new(new(),50)))).Items);
        async Task<bool> Enroll() { try { await f.SendAsync(new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,InstanceFixture.Secret()),g.Proof); return true; } catch(RequestRejectedException e) when(e.Failure is RequestFailure.InvalidState or RequestFailure.ResourceNotFound) { return false; } }
        async Task<bool> Revoke() { try { await f.SendAsync(new RevokeBindingCommand(Guid.NewGuid(),f.Device.Id,f.SoftwareId,b.Revision,"并发撤销")); return true; } catch(RequestRejectedException e) when(e.Failure==RequestFailure.InvalidState) { return false; } }
        var results=await Task.WhenAll(Enroll(),Revoke()); Assert.Equal(1,results.Count(x=>x));
        var count=await f.CountAsync("ins.instances"); Assert.Equal(results[0]?1:0,count); var bindings=await f.SendAsync(new ListBindingsQuery(f.Device.Id,new(new(),50))); Assert.Equal(count==1,bindings.Items.Count==1);
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task RegistrationFailureRollsBackQuotaBindingReferenceCredentialAuditAndResult(string stage)
    {
        await using var f=await InstanceFixture.CreateAsync(); var g=await f.GrantAsync(1); var audit=await f.CountAsync("aud.events");
        using var ct=new CancellationTokenSource(); IInterceptor? interceptor=stage=="save"?new SaveFailure():stage=="cancel"?new SaveCancellation(ct):null;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.SendAsync(new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,InstanceFixture.Secret()),g.Proof,interceptor:interceptor,auditFailure:stage=="audit",token:ct.Token));
        foreach(var table in new[] { "ins.instances","iam.instance_subjects","iam.instance_credentials","iam.registrations","ins.instance_snapshots" }) Assert.Equal(0,await f.CountAsync(table));
        Assert.Equal(audit,await f.CountAsync("aud.events")); Assert.Equal(0,Assert.Single((await f.SendAsync(new ListEnrollmentGrantsQuery(f.SoftwareId))).Items).UsedCount);
        var b=Assert.Single((await f.SendAsync(new ListBindingsQuery(f.Device.Id,new(new(),50)))).Items); await f.SendAsync(new RevokeBindingCommand(Guid.NewGuid(),f.Device.Id,f.SoftwareId,b.Revision,"引用随回滚清除"));
    }
    [Fact]
    public async Task LostCommitConfirmationVerifiesInNewScopeAndDoesNotCreateAgain()
    {
        await using var f=await InstanceFixture.CreateAsync(); var g=await f.GrantAsync(); var loss=new LostConfirmation(); var x=new RegisterInstanceCommand(Guid.NewGuid(),f.SoftwareId,Guid.NewGuid(),f.Device.Id,InstanceFixture.Secret());
        var r=(await f.SendAsync(x,g.Proof,interceptor:loss)).Value; Assert.Equal(1,loss.Commits); Assert.Equal(r,(await f.SendAsync(x,g.Proof)).Value); Assert.Equal(1,await f.CountAsync("ins.instances")); Assert.Equal(1,await f.CountAsync("iam.registrations"));
    }
    [Fact]
    public async Task RecoveryIsSingleUseRevokesAcrossScopesKeepsHistoryAndInvalidatesOldStreams()
    {
        await using var f=await InstanceFixture.CreateAsync(); var e=await f.EnrollAsync(); var id=e.Registration.InstanceId;
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),0),e.Proof,id); await f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report()),e.Proof,id);
        var grantSecret=InstanceFixture.Secret(); var g=(await f.SendAsync(new CreateRecoveryGrantCommand(Guid.NewGuid(),id,f.Clock.GetUtcNow().AddHours(1),grantSecret,"夹具身份恢复"))).Value;
        var rp=new AccessProof(ActorKind.RecoveryGrant,g.Id,grantSecret); var x=new RecoverInstanceCommand(Guid.NewGuid(),InstanceFixture.Secret());
        var r=(await f.SendAsync(x,rp,id)).Value; Assert.Equal(id,r.InstanceId); Assert.Equal(2,r.StreamEpoch); Assert.Equal(r,(await f.SendAsync(x,rp,id)).Value);
        await Rejected(RequestFailure.CredentialInvalid,()=>f.SendAsync(new GetClientContextQuery(),e.Proof,id)); await Rejected(RequestFailure.CredentialInvalid,()=>f.SendAsync(e.Command,e.GrantProof));
        await Rejected(RequestFailure.RegistrationConflict,()=>f.SendAsync(x with { Key=Guid.NewGuid() },rp,id));
        var proof=new AccessProof(ActorKind.Instance,r.CredentialId,x.SecretMaterial); var context=await f.SendAsync(new GetClientContextQuery(),proof,id); Assert.Equal(2,context.CurrentEpoch);
        Assert.False((await f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report()),proof,id)).Value.Applied);
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),2),proof,id); await f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report(3,1,"2.0.0")),proof,id);
        Assert.Equal(2,(await f.SendAsync(new GetInstanceHistoryQuery(id))).Items.Count); Assert.Equal(2,(await f.SendAsync(new ListInstanceCredentialsQuery(id))).Items.Count);
        var creds=(await f.SendAsync(new ListInstanceCredentialsQuery(id))).Items; Assert.Single(creds,c=>c.RevokedAt is null); Assert.Equal(1,await f.CountAsync("ins.instances"));
    }
    [Theory][InlineData("audit")][InlineData("save")][InlineData("cancel")]
    public async Task FailedRecoveryRetainsOriginalCredentialStreamAndUnconsumedGrant(string stage)
    {
        await using var f=await InstanceFixture.CreateAsync(); var e=await f.EnrollAsync(); var id=e.Registration.InstanceId;
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),0),e.Proof,id);
        var secret=InstanceFixture.Secret(); var grant=(await f.SendAsync(new CreateRecoveryGrantCommand(Guid.NewGuid(),id,f.Clock.GetUtcNow().AddHours(1),secret,"恢复回滚夹具"))).Value;
        var proof=new AccessProof(ActorKind.RecoveryGrant,grant.Id,secret); var command=new RecoverInstanceCommand(Guid.NewGuid(),InstanceFixture.Secret()); var audit=await f.CountAsync("aud.events");
        using var ct=new CancellationTokenSource(); IInterceptor? interceptor=stage=="save"?new SaveFailure():stage=="cancel"?new SaveCancellation(ct):null;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.SendAsync(command,proof,id,interceptor,auditFailure:stage=="audit",token:ct.Token));
        Assert.Equal(1,await f.CountAsync("iam.instance_credentials")); Assert.Equal(audit,await f.CountAsync("aud.events"));
        Assert.Equal(1,(await f.SendAsync(new GetClientContextQuery(),e.Proof,id)).CurrentEpoch);
        Assert.True((await f.SendAsync(new SubmitStatusReportCommand(InstanceFixture.Report()),e.Proof,id)).Value.Applied);
        var recovered=(await f.SendAsync(command,proof,id)).Value; Assert.Equal(2,recovered.StreamEpoch); Assert.Equal(2,await f.CountAsync("iam.instance_credentials"));
        await Rejected(RequestFailure.CredentialInvalid,()=>f.SendAsync(new GetClientContextQuery(),e.Proof,id));
    }
    [Fact]
    public async Task LostRecoveryCommitConfirmationVerifiesOriginalResultWithoutSecondCredentialOrEpoch()
    {
        await using var f=await InstanceFixture.CreateAsync(); var e=await f.EnrollAsync(); var id=e.Registration.InstanceId;
        await f.SendAsync(new OpenReportStreamCommand(Guid.NewGuid(),0),e.Proof,id);
        var secret=InstanceFixture.Secret(); var grant=(await f.SendAsync(new CreateRecoveryGrantCommand(Guid.NewGuid(),id,f.Clock.GetUtcNow().AddHours(1),secret,"恢复确认夹具"))).Value;
        var proof=new AccessProof(ActorKind.RecoveryGrant,grant.Id,secret); var command=new RecoverInstanceCommand(Guid.NewGuid(),InstanceFixture.Secret()); var loss=new LostConfirmation();
        var result=(await f.SendAsync(command,proof,id,interceptor:loss)).Value; var audit=await f.CountAsync("aud.events");
        Assert.Equal(1,loss.Commits); Assert.Equal(2,result.StreamEpoch); Assert.Equal(result,(await f.SendAsync(command,proof,id)).Value);
        Assert.Equal(2,await f.CountAsync("iam.instance_credentials")); Assert.Equal(audit,await f.CountAsync("aud.events"));
        Assert.Equal(2,(await f.SendAsync(new GetClientContextQuery(),new(ActorKind.Instance,result.CredentialId,command.SecretMaterial),id)).CurrentEpoch);
    }
    [Fact]
    public async Task CurrentHumanAuthorizationPrecedesReplayAndSecretsAreUnreadableToQueryRole()
    {
        await using var f=await InstanceFixture.CreateAsync(); var g=await f.GrantAsync();
        var command=new CreateEnrollmentGrantCommand(Guid.NewGuid(),f.SoftwareId,[f.Device.Id],f.Clock.GetUtcNow().AddHours(1),1,InstanceFixture.Secret(),"授权重放夹具"); await f.SendAsync(command);
        var count=await f.CountAsync("iam.enrollment_grants"); var u=await f.Site.UserAsync(f.Site.Proof.SubjectId);
        await f.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),u.Id,u.Revision,u.Permissions.Where(p=>p.Operation!="enrollment.manage").ToArray(),"撤销夹具授权"));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(new ListEnrollmentGrantsQuery(f.SoftwareId)));
        await Rejected(RequestFailure.ResourceNotFound,()=>f.SendAsync(command)); Assert.Equal(count,await f.CountAsync("iam.enrollment_grants"));
        await Assert.ThrowsAnyAsync<Exception>(()=>PersistenceDatabase.ScalarAsync<string>(f.Site.Personnel.Personnel.Database.ReaderConnection,"SELECT \"SecretHash\" FROM iam.enrollment_grants LIMIT 1"));
        Assert.DoesNotContain(g.Proof.Secret,g.Proof.ToString()); Assert.DoesNotContain(g.Proof.Secret,new RecoverInstanceCommand(Guid.NewGuid(),g.Proof.Secret).ToString());
    }
    internal static async Task Rejected<T>(RequestFailure failure,Func<Task<T>> action)=>Assert.Equal(failure,(await Assert.ThrowsAsync<RequestRejectedException>(async()=> { await action(); })).Failure);
    internal sealed class SaveFailure : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken ct=default)=>throw new InvalidOperationException("fixture save failure"); }
    internal sealed class SaveCancellation(CancellationTokenSource ct) : SaveChangesInterceptor
    { public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,InterceptionResult<int> result,CancellationToken token=default) { ct.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.FromResult(result); } }
    internal sealed class LostConfirmation : DbTransactionInterceptor
    { public int Commits; public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData data,CancellationToken token=default) { Commits++; throw new IOException("fixture lost commit confirmation"); } }
}
