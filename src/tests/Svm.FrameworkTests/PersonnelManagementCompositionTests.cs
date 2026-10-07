using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.AuditService;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.IdentityService;
using Svm.Security;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersonnelManagementCompositionTests
{
    private static IServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSvmPersonnelManagementApplication();
        services.AddSvmPostgres("Host=127.0.0.1;Database=composition_fixture;Username=fixture_writer;Password=composition_dummy;Timeout=1;CommandTimeout=1");
        services.AddSvmReadPersistence("Host=127.0.0.1;Database=composition_fixture;Username=fixture_reader;Password=composition_dummy;Timeout=1;CommandTimeout=1").AddSvmUserQueries();
        services.AddSvmPersonnel().AddSvmPersonnelAdministration().AddSvmAudit()
            .AddSvmPersonnelCrypto(new(8, 15, 128, 600000, 5, 60, 10, 60));
        services.AddSingleton(new PersonnelManagementOptions());
        services.AddScoped<ITrustedCallContextSource, Source>(); services.AddScoped<ISessionProofSource, Source>();
        return services;
    }
    [Fact]
    public void ClosedAdministrationCompositionBuildsWithFourTypedAdapters()
    {
        var services = Services(); services.ValidateSvmFoundation();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Equal(4, services.Count(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IIdempotencyRequestAdapter<,>)));
        Assert.Equal(4, provider.GetRequiredService<RequestCatalog>().Bindings.Count(b => PersonnelManagementCapabilities.Contains(b.RequestType)));
    }
    [Theory]
    [InlineData("missing")] [InlineData("singleton")] [InlineData("factory")] [InlineData("duplicate")] [InlineData("keyed")]
    public void MissingOrBypassedAdapterRegistrationIsRejected(string mode)
    {
        var services = Services(); var contract = typeof(IIdempotencyRequestAdapter<CreateUserCommand, OperationResult<UserView>>);
        var descriptor = services.Single(d => d.ServiceType == contract);
        if (mode is "missing" or "singleton" or "factory") services.Remove(descriptor);
        if (mode == "singleton") services.Add(new(contract, descriptor.ImplementationType!, ServiceLifetime.Singleton));
        if (mode == "factory") services.Add(new(contract, _ => new object(), ServiceLifetime.Scoped));
        if (mode == "duplicate") services.Add(descriptor);
        if (mode == "keyed") services.Add(new(contract, "fixture", descriptor.ImplementationType!, ServiceLifetime.Scoped));
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }
    [Fact]
    public void CopiedPolicyDoesNotActivateAnUnapprovedCommand()
    {
        Assert.Throws<InvalidOperationException>(() => new RequestCatalog([new(typeof(CopiedCommand), typeof(CopiedHandler))]));
    }
    [Fact]
    public void ManagementOptionsRejectAnUnboundedPageOrCursorWindow()
    {
        foreach (var options in new[] { new PersonnelManagementOptions(0), new PersonnelManagementOptions(50, 201), new PersonnelManagementOptions(CursorMinutes: 61) })
            Assert.Throws<RequestRejectedException>(options.Validate);
    }
    [RequestPolicy("identity.users.create", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
        TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "identity.manage", ValidationReason = "Fixture copied metadata.")]
    private sealed class CopiedCommand : ICommand<UserView>;
    private sealed class CopiedHandler : IRequestHandler<CopiedCommand, OperationResult<UserView>>
    {
        public Task<OperationResult<UserView>> Handle(CopiedCommand request, CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class Source : ITrustedCallContextSource, ISessionProofSource
    {
        public Source() { }
        public SessionProof? Proof => null;
        public string SourceAddress => "composition-fixture";
        public CallContextSnapshot? GetCurrent() => null;
    }
}
