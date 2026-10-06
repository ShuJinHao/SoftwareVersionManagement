using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.SecurityTests;

[Trait("Category", "Security")]
public sealed class PersonnelBoundaryTests
{
    [Fact]
    public void ReusingApprovedOperationNameCannotActivateAnotherCommand()
    {
        Assert.False(PersonnelWriteCapabilities.Contains(typeof(ImpersonatedLogin)));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSvmRequestPipeline([
            new RequestBinding(typeof(ImpersonatedLogin), typeof(ImpersonatedHandler))]));
    }

    [Fact]
    public void HostsExposeOnlyTheirSpecificPersonnelCapabilities()
    {
        using var http = new ServiceCollection().AddSvmSessionApplication().BuildServiceProvider();
        var httpCatalog = http.GetRequiredService<RequestCatalog>();
        Assert.Throws<RequestRejectedException>(() => httpCatalog.GetPolicy(typeof(SeedPersonnelCommand)));
        using var migration = new ServiceCollection().AddSvmSeedApplication().BuildServiceProvider();
        Assert.Throws<RequestRejectedException>(() => migration.GetRequiredService<RequestCatalog>().GetPolicy(typeof(LoginCommand)));
        using var worker = new ServiceCollection().AddSvmApplication().BuildServiceProvider();
        Assert.Throws<RequestRejectedException>(() => worker.GetRequiredService<RequestCatalog>().GetPolicy(typeof(LoginCommand)));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSvmSessionApplication().ValidateSvmFoundation());
    }

    [Fact]
    public void SecretBearingContractsNeverIncludeSecretsInDiagnostics()
    {
        var secret = Guid.NewGuid().ToString("N");
        object[] inputs = [new LoginCommand("TEST", secret), new ChangePasswordCommand(secret, secret), new SeedPersonnelCommand("TEST", "Test", secret),
            new SessionProof(Guid.NewGuid(), Guid.NewGuid(), secret)];
        Assert.All(inputs, input => Assert.False(input.ToString()!.Contains(secret, StringComparison.Ordinal)));
    }

    [RequestPolicy("session.login", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global, TransactionMode.DatabaseAtomic,
        IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Anonymous, ValidationReason = "Malicious copied metadata test.")]
    public sealed class ImpersonatedLogin : ICommand<PersonnelMutation>;
    public sealed class ImpersonatedHandler : IRequestHandler<ImpersonatedLogin, OperationResult<PersonnelMutation>>
    {
        public Task<OperationResult<PersonnelMutation>> Handle(ImpersonatedLogin request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unapproved handler must never execute.");
    }
}
