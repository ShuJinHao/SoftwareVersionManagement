using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Application;
using Svm.AuditService;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.IdentityService;
using Svm.InstanceService;
using Svm.ReleaseService;
using Svm.Security;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.CrossCutting.Registration;

namespace Svm.FrameworkTests;

internal sealed class InstanceFixture(SiteCatalogFixture site) : IAsyncDisposable
{
    internal SiteCatalogFixture Site {get;}=site;
    internal TestClock Clock {get;}=new();
    internal static InstanceAccessOptions Limits {get;}=new(172800,100,172800);
    internal Guid SoftwareId {get;private set;}
    internal DeviceView Device {get;private set;}=null!;
    internal static async Task<InstanceFixture> CreateAsync()
    {
        var f=new InstanceFixture(await SiteCatalogFixture.CreateAsync());
        try
        {
            f.SoftwareId=(await f.Site.SoftwareAsync()).Value.Id; f.Device=(await f.Site.DeviceAsync()).Device;
            await f.Site.SendAsync(new CreateBindingCommand(Guid.NewGuid(),f.Device.Id,f.SoftwareId,"夹具接入映射"));
            var u=await f.Site.UserAsync(f.Site.Proof.SubjectId);
            await f.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(),u.Id,u.Revision,u.Permissions.Append(new(f.SoftwareId,"enrollment.manage")).ToArray(),"夹具显式接入授权"));
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal ServiceProvider Provider(AccessProof? access=null,Guid? instanceId=null,IInterceptor? interceptor=null,bool auditFailure=false,SessionProof? human=null,Guid? softwareId=null)
    {
        var s=new ServiceCollection(); s.AddSvmInstanceApplication(); s.AddSingleton(Limits);
        s.AddSvmPostgres(Site.Personnel.Personnel.Database.WriterConnection).AddSvmReadPersistence(Site.Personnel.Personnel.Database.ReaderConnection).AddSvmUserQueries().AddSvmCatalogQueries();
        s.AddSvmPersonnel().AddSvmPersonnelAdministration().AddSvmPersonnelSoftwareAdministration().AddSvmSoftwareCatalog().AddSvmSiteAssets().AddSvmAudit().AddSvmPersonnelCrypto(Site.Personnel.Personnel.Policy).AddSvmInstanceAccess().AddSvmManagedInstances();
        s.AddSingleton(new PersonnelManagementOptions()); s.AddSingleton(Site.Options); s.AddSingleton<TimeProvider>(Clock);
        s.AddScoped<ISessionProofSource>(_=>new Context(human??Site.Proof,access,softwareId??SoftwareId,instanceId));
        s.AddScoped<IAccessProofSource>(_=>new Context(human??Site.Proof,access,softwareId??SoftwareId,instanceId));
        s.AddScoped<ITrustedCallContextSource>(_=>new Context(human??Site.Proof,access,softwareId??SoftwareId,instanceId));
        if(interceptor is not null) s.AddSingleton(interceptor);
        if(auditFailure) s.Replace(ServiceDescriptor.Scoped<IAuditWriter,FailingAudit>());
        s.ValidateSvmFoundation(); return s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild=true,ValidateScopes=true });
    }
    internal async Task<T> SendAsync<T>(IRequest<T> x,AccessProof? access=null,Guid? instanceId=null,IInterceptor? interceptor=null,bool auditFailure=false,CancellationToken token=default,SessionProof? human=null,Guid? softwareId=null)
    { await using var p=Provider(access,instanceId,interceptor,auditFailure,human,softwareId); await using var scope=p.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISender>().Send(x,token); }
    internal async Task<(GrantView Grant,AccessProof Proof)> GrantAsync(int capacity=10,DateTimeOffset? expires=null)
    {
        var secret=Secret(); var result=await SendAsync(new CreateEnrollmentGrantCommand(Guid.NewGuid(),SoftwareId,[Device.Id],expires??Clock.GetUtcNow().AddHours(1),capacity,secret,"夹具登记许可"));
        return (result.Value,new(ActorKind.EnrollmentGrant,result.Value.Id,secret));
    }
    internal async Task<(RegistrationResult Registration,AccessProof Proof,RegisterInstanceCommand Command,AccessProof GrantProof)> EnrollAsync()
    {
        var g=await GrantAsync(); var secret=Secret(); var command=new RegisterInstanceCommand(Guid.NewGuid(),SoftwareId,Guid.NewGuid(),Device.Id,secret);
        var r=(await SendAsync(command,g.Proof)).Value; return (r,new(ActorKind.Instance,r.CredentialId,secret),command,g.Proof);
    }
    internal Task<long> CountAsync(string table)=>table=="iam.registrations"?PersistenceDatabase.ScalarAsync<long>(Site.Personnel.Personnel.Database.MigrationConnection,"SELECT count(*) FROM iam.registrations"):Site.CountAsync(table);
    internal static string Secret()=>Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).Replace('+','-').Replace('/','_').TrimEnd('=');
    internal static StateReport Report(long epoch=1,long sequence=1,string? version="1.2.3")=>new(epoch,sequence,DateTimeOffset.UtcNow,"Installed",null,version,null,"Running",["192.0.2.10"],new("None",[]));
    public ValueTask DisposeAsync()=>Site.DisposeAsync();
    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow()=>_now;
        internal void Advance(TimeSpan value)=>_now+=value;
    }
    private sealed class Context(SessionProof human,AccessProof? access,Guid software,Guid? instance) : ITrustedCallContextSource,ISessionProofSource,IAccessProofSource
    {
        SessionProof? ISessionProofSource.Proof=>access is null?human:null;
        AccessProof? IAccessProofSource.Proof=>access;
        public string SourceAddress=>"instance-fixture";
        public CallContextSnapshot GetCurrent()=> access is null ? new(new(ActorKind.Human,human.SubjectId),RequestKind.Manage,"instance-fixture") :
            new(new(access.Kind,access.Kind==ActorKind.Instance?instance!.Value:access.Id,software,access.Kind==ActorKind.EnrollmentGrant?null:instance),
                access.Kind switch { ActorKind.Instance=>RequestKind.Client,ActorKind.EnrollmentGrant=>RequestKind.Enrollment,_=>RequestKind.Recovery },"instance-fixture");
    }
    private sealed class FailingAudit : IAuditWriter { public void Append(AuditFact fact)=>throw new InvalidOperationException("fixture audit failure"); }
}
