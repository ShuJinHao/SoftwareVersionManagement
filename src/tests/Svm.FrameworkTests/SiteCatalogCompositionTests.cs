using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.AuditService;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.IdentityService;
using Svm.InstanceService;
using Svm.ReleaseService;
using Svm.Security;
using Svm.ServiceDefaults;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class SiteCatalogCompositionTests
{
    private static IServiceCollection Services()
    {
        var services = new ServiceCollection(); services.AddSvmSiteCatalogApplication();
        services.AddSvmPostgres("Host=127.0.0.1;Database=composition_fixture;Username=fixture_writer;Password=composition_dummy;Timeout=1;CommandTimeout=1");
        services.AddSvmReadPersistence("Host=127.0.0.1;Database=composition_fixture;Username=fixture_reader;Password=composition_dummy;Timeout=1;CommandTimeout=1").AddSvmUserQueries().AddSvmCatalogQueries();
        services.AddSvmPersonnel().AddSvmPersonnelAdministration().AddSvmPersonnelSoftwareAdministration().AddSvmAudit().AddSvmSiteAssets().AddSvmSoftwareCatalog()
            .AddSvmPersonnelCrypto(new(8, 15, 128, 600000, 5, 60, 10, 60));
        services.AddSingleton(new PersonnelManagementOptions()); services.AddSingleton(new SiteCatalogOptions());
        services.AddScoped<ITrustedCallContextSource, Source>(); services.AddScoped<ISessionProofSource, Source>(); return services;
    }
    [Fact]
    public void ClosedCompositionContainsOnlyEightAdditionalWritesAndTenQueries()
    {
        var services = Services(); services.ValidateSvmFoundation(); using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var bindings = provider.GetRequiredService<RequestCatalog>().Bindings;
        Assert.Equal(8, bindings.Count(b => CatalogCapabilities.IsWrite(b.RequestType))); Assert.Equal(10, bindings.Count(b => CatalogCapabilities.IsQuery(b.RequestType)));
        Assert.Equal(12, services.Count(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IIdempotencyRequestAdapter<,>)));
    }
    [Theory] [InlineData("missing")] [InlineData("singleton")] [InlineData("factory")] [InlineData("duplicate")]
    public void InvalidCatalogAdapterRegistrationIsRejected(string mode)
    {
        var services = Services(); var type = typeof(IIdempotencyRequestAdapter<CreateSoftwareCommand, OperationResult<SoftwareView>>); var descriptor = services.Single(d => d.ServiceType == type);
        if (mode != "duplicate") services.Remove(descriptor);
        if (mode == "singleton") services.Add(new(type, descriptor.ImplementationType!, ServiceLifetime.Singleton));
        if (mode == "factory") services.Add(new(type, _ => new object(), ServiceLifetime.Scoped));
        if (mode == "duplicate") services.Add(descriptor);
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }
    [Fact]
    public void CopiedCatalogPolicyDoesNotOpenAnotherBusinessCommand() =>
        Assert.Throws<InvalidOperationException>(() => new RequestCatalog([new(typeof(Copied), typeof(Handler))]));
    [Fact]
    public void ConfigurationHasNoFactoryDefaultsAndRejectsInvalidLimitsOrTimeZone()
    {
        Assert.Null(SiteConfiguration.Load(null).SiteId);
        foreach (var option in new[] { new SiteCatalogOptions(), new(Guid.Empty, "夹具", "Asia/Shanghai"), new(Guid.NewGuid(), "夹具", "invalid_zone"),
            new(Guid.NewGuid(), "夹具", "Asia/Shanghai", MaximumPageSize: 201), new(Guid.NewGuid(), "夹具", "Asia/Shanghai", CursorMinutes: 61) })
            Assert.Equal(RequestFailure.ConfigurationInvalid, Assert.Throws<RequestRejectedException>(option.Validate).Failure);
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, """{"siteId":"11111111-1111-4111-8111-111111111111","siteName":"夹具","siteTimeZone":"Asia/Shanghai","secret":"fixture-secret"}""");
            var error = Assert.Throws<RequestRejectedException>(() => SiteConfiguration.Load(path)); Assert.Equal(RequestFailure.ConfigurationInvalid, error.Failure); PersistenceDatabase.AssertRedacted(error.ToString(), "fixture-secret"); }
        finally { File.Delete(path); }
    }
    [RequestPolicy("rel.software.create", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic,
        IdempotencyMode.OperationResult, ValidationMode.ExplicitlyNone, ActorKind.Human, Permission = "software.create", ValidationReason = "Copied fixture.")]
    private sealed class Copied : ICommand<SoftwareView>;
    private sealed class Handler : IRequestHandler<Copied, OperationResult<SoftwareView>> { public Task<OperationResult<SoftwareView>> Handle(Copied request, CancellationToken token) => throw new InvalidOperationException(); }
    private sealed class Source : ITrustedCallContextSource, ISessionProofSource { public Source() { } public SessionProof? Proof => null; public string SourceAddress => "composition"; public CallContextSnapshot? GetCurrent() => null; }
}
