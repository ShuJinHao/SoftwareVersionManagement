using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.AuditService;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.IdentityService;
using Svm.InstanceService;
using Svm.ReleaseService;
using Svm.Security;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class InstanceCompositionTests
{
    private static IServiceCollection Services()
    {
        var s=new ServiceCollection(); s.AddSvmInstanceApplication();
        s.AddSvmPostgres("Host=127.0.0.1;Database=composition_fixture;Username=fixture_writer;Password=composition_dummy;Timeout=1;CommandTimeout=1");
        s.AddSvmReadPersistence("Host=127.0.0.1;Database=composition_fixture;Username=fixture_reader;Password=composition_dummy;Timeout=1;CommandTimeout=1").AddSvmUserQueries().AddSvmCatalogQueries();
        s.AddSvmPersonnel().AddSvmPersonnelAdministration().AddSvmPersonnelSoftwareAdministration().AddSvmAudit().AddSvmSiteAssets().AddSvmSoftwareCatalog().AddSvmInstanceAccess().AddSvmManagedInstances().AddSvmPersonnelCrypto(new(8,15,128,600000,5,60,10,60));
        s.AddSingleton(new PersonnelManagementOptions()); s.AddSingleton(new SiteCatalogOptions()); s.AddSingleton(TimeProvider.System);
        s.AddScoped<ITrustedCallContextSource,Context>(); s.AddScoped<ISessionProofSource,Context>(); s.AddScoped<IAccessProofSource,Context>(); return s;
    }
    [Fact]
    public void CompositionActivatesExactlyTenInstanceWritesAndSixQueriesWithTypedStrategies()
    {
        var s=Services(); s.ValidateSvmFoundation(); using var p=s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild=true,ValidateScopes=true }); var c=p.GetRequiredService<RequestCatalog>();
        Assert.Equal(10,c.Bindings.Count(x=>InstanceCapabilities.IsWrite(x.RequestType))); Assert.Equal(6,c.Bindings.Count(x=>InstanceCapabilities.IsQuery(x.RequestType)));
        Assert.Equal(IdempotencyMode.EnrollmentProtocol,c.GetPolicy(typeof(RegisterInstanceCommand)).Idempotency); Assert.Equal(IdempotencyMode.ReportSequence,c.GetPolicy(typeof(SubmitStatusReportCommand)).Idempotency);
        Assert.Equal(3,s.Count(x=>x.ServiceType.IsGenericType && x.ServiceType.GetGenericTypeDefinition()==typeof(IProtocolRequestAdapter<,>)));
    }
    [Theory][InlineData("missing")][InlineData("singleton")][InlineData("duplicate")]
    public void MissingOrBypassedNaturalProtocolAdapterFailsStartup(string mode)
    {
        var s=Services(); var d=s.Single(x=>x.ServiceType==typeof(IProtocolRequestAdapter<SubmitStatusReportCommand,OperationResult<ReportResult>>));
        if(mode!="duplicate") s.Remove(d);
        if(mode=="singleton") s.Add(new(d.ServiceType,d.ImplementationType!,ServiceLifetime.Singleton)); if(mode=="duplicate") s.Add(d);
        Assert.Throws<InvalidOperationException>(()=>s.ValidateSvmFoundation());
    }
    [Fact]
    public void MissingSigningLimitsDisableOnlyNewGrantIssuance()
    {
        var options=Svm.ServiceDefaults.InstanceAccessConfiguration.Load(null);
        Assert.Equal(RequestFailure.ConfigurationInvalid,Assert.Throws<RequestRejectedException>(()=>options.CheckEnrollment(DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddHours(1),1)).Failure);
        Assert.Equal(RequestFailure.ConfigurationInvalid,Assert.Throws<RequestRejectedException>(()=>options.CheckRecovery(DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddHours(1))).Failure);
        Services().ValidateSvmFoundation(); // Existing personnel composition remains available without signing limits.
    }
    [Fact]
    public void GrantExpiryAndQuotaCannotExceedExplicitDeploymentLimits()
    {
        var options=new InstanceAccessOptions(60,2,30); var now=DateTimeOffset.UtcNow;
        options.CheckEnrollment(now,now.AddSeconds(60),2); options.CheckRecovery(now,now.AddSeconds(30));
        Assert.Throws<RequestRejectedException>(()=>options.CheckEnrollment(now,now.AddSeconds(61),2));
        Assert.Throws<RequestRejectedException>(()=>options.CheckEnrollment(now,now.AddSeconds(60),3));
        Assert.Throws<RequestRejectedException>(()=>options.CheckRecovery(now,now.AddSeconds(31)));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enrollmentMaxLifetimeSeconds\":60,\"enrollmentMaxCount\":1,\"recoveryMaxLifetimeSeconds\":0}")]
    [InlineData("{\"enrollmentMaxLifetimeSeconds\":60,\"enrollmentMaxCount\":1,\"recoveryMaxLifetimeSeconds\":30,\"extra\":true}")]
    public void ExplicitInvalidSigningConfigurationIsRejected(string json)
    {
        var file=Path.Combine(Path.GetTempPath(),"svm-instance-options-"+Guid.NewGuid().ToString("N")+".json");
        try { File.WriteAllText(file,json); Assert.Equal(RequestFailure.ConfigurationInvalid,Assert.Throws<RequestRejectedException>(()=>Svm.ServiceDefaults.InstanceAccessConfiguration.Load(file)).Failure); }
        finally { File.Delete(file); }
    }
    private sealed class Context : ITrustedCallContextSource,ISessionProofSource,IAccessProofSource
    { public CallContextSnapshot? GetCurrent()=>null; SessionProof? ISessionProofSource.Proof=>null; AccessProof? IAccessProofSource.Proof=>null; public string SourceAddress=>"composition-fixture"; }
}
