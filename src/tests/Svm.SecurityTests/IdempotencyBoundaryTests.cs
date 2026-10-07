using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.SecurityTests;

[Trait("Category", "Security")]
public sealed class IdempotencyBoundaryTests
{
    [Fact]
    public void OwnerAndSubjectCannotBePassedIntoStorageOrConstructedAsAPublicCapability()
    {
        Assert.Empty(typeof(OperationIdentity).GetConstructors());
        Assert.All(typeof(IOperationResultStore).GetMethods(), method =>
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(ModuleOwner) || p.ParameterType == typeof(CallActor)));
        Assert.All(typeof(IOperationContext).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void ForgedOperationContextCannotReplaceTheFrameworkScopedBinding()
    {
        var services = new ServiceCollection().AddSvmRequestPipeline([]);
        services.RemoveAll<IOperationContext>();
        services.AddScoped<IOperationContext, ForgedContext>();
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }

    [Fact]
    public void IdempotencyCannotBeRemovedOrMovedAheadOfAuthorization()
    {
        var services = new ServiceCollection().AddSvmRequestPipeline([]);
        var behaviors = services.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>)).ToArray();
        var idempotency = Assert.Single(behaviors, d => d.ImplementationType!.Name.StartsWith("IdempotencyBehavior", StringComparison.Ordinal));
        services.Remove(idempotency);
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
        services.Insert(services.IndexOf(behaviors[2]), idempotency);
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }

    [Fact]
    public void ARequestAdapterCannotActivatePersistentCommandsOrCacheSessionResponses()
    {
        var services = new ServiceCollection().AddSvmRequestPipeline([]);
        services.AddScoped<IIdempotencyRequestAdapter<string, string>, UnapprovedAdapter>();
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }

    private sealed class ForgedContext : IOperationContext { public OperationIdentity? Current => null; }
    private sealed class UnapprovedAdapter : IIdempotencyRequestAdapter<string, string>
    {
        public OperationRequestData Describe(string request) => throw new NotSupportedException();
        public OperationResultReference GetReference(string response) => throw new NotSupportedException();
        public Task<string> RestoreAsync(OperationResultReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
