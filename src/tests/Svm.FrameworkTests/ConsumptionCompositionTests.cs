using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.EventBus;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;
using Svm.Services.CrossCutting.Consumption;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class ConsumptionCompositionTests
{
    [Fact]
    public void EmptyDirectoryDoesNotActivateAConsumerOrInboxCleaner()
    {
        var services = new ServiceCollection().AddSvmRequestPipeline([]).AddSvmConsumption([]).AddSvmMessaging(OutboxFixture.Options(), true);
        services.ValidateSvmFoundation();
        Assert.DoesNotContain(services, d => d.ImplementationType?.Name.Contains("InboxCleanup", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IIntegrationEventPreflight));
        Assert.Throws<IntegrationConsumptionException>(() => services.AddSvmConsumption([]));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown-type")]
    [InlineData("wrong-handler")]
    [InlineData("unsealed")]
    [InlineData("null")]
    public void IllegalDirectoriesAreRejected(string fault)
    {
        var b = ConsumptionFixture.Binding;
        IntegrationConsumerBinding[] bindings = fault switch
        {
            "duplicate" => [b,b], "unknown-type" => [new(typeof(object), b.HandlerType)],
            "wrong-handler" => [new(typeof(PackageWorkAvailableV1), b.HandlerType)],
            "unsealed" => [new(b.EventType, typeof(ConsumptionTestHandler))], _ => [null!]
        };
        Assert.Throws<IntegrationConsumptionException>(() => new ServiceCollection().AddSvmConsumption(bindings));
    }
    [Theory]
    [InlineData("missing-handler")]
    [InlineData("duplicate-handler")]
    [InlineData("singleton-handler")]
    [InlineData("transient-handler")]
    [InlineData("keyed-handler")]
    [InlineData("missing-authorizer")]
    [InlineData("singleton-authorizer")]
    [InlineData("preflight-bypass")]
    [InlineData("typed-bypass")]
    public void RegistrationAndLifetimeCannotBypassTheDirectory(string fault)
    {
        var b = ConsumptionFixture.Binding;
        var services = new ServiceCollection().AddSvmConsumption([b]);
        services.AddScoped<IIntegrationWorkAuthorizer, NeverAuthorizer>(); services.AddScoped<IUnitOfWork, NeverUnit>();
        services.AddScoped<IOperationResultStore, NeverResults>();
        services.AddScoped<IIntegrationConsumptionTransaction,NeverTransaction>();
        switch (fault)
        {
            case "missing-handler": services.RemoveAll(b.HandlerType); break;
            case "duplicate-handler": services.AddScoped(b.HandlerType); break;
            case "singleton-handler": services.Replace(ServiceDescriptor.Singleton(b.HandlerType, b.HandlerType)); break;
            case "transient-handler": services.Replace(ServiceDescriptor.Transient(b.HandlerType, b.HandlerType)); break;
            case "keyed-handler": services.AddKeyedScoped(b.HandlerType,"hidden",b.HandlerType); break;
            case "missing-authorizer": services.RemoveAll<IIntegrationWorkAuthorizer>(); break;
            case "singleton-authorizer": services.Replace(ServiceDescriptor.Singleton<IIntegrationWorkAuthorizer,NeverAuthorizer>()); break;
            case "preflight-bypass": services.RemoveAll<IIntegrationEventPreflight>(); break;
            case "typed-bypass": services.AddScoped<IIntegrationEventHandler<TaskPreparationAvailableV1>,ConsumptionTestHandler>(); break;
        }
        Assert.Throws<IntegrationConsumptionException>(() => services.ValidateSvmConsumption());
    }
    [Theory]
    [InlineData(0,16,30)]
    [InlineData(65,256,30)]
    [InlineData(4,3,30)]
    [InlineData(4,257,30)]
    [InlineData(4,16,0)]
    [InlineData(4,16,1441)]
    public void ConsumerBoundsAreValidated(int concurrency, int prefetch, int window) => Assert.Throws<OutboxException>(() =>
        (OutboxFixture.Options() with { ConsumerConcurrency=concurrency,ConsumerPrefetch=prefetch,InboxWindowMinutes=window }).Validate());

    [Theory]
    [InlineData("missing-transport-binding")]
    [InlineData("wrong-handler")]
    [InlineData("wrong-event")]
    public void TransportAndApplicationDirectoriesMustAgree(string change)
    {
        var services=new ServiceCollection().AddSvmRequestPipeline([]).AddSvmConsumption([ConsumptionFixture.Binding]);
        IntegrationConsumerBinding[] transport=change switch
        {
            "missing-transport-binding"=>[], "wrong-handler"=>[new(ConsumptionFixture.Binding.EventType,typeof(ConsumptionTestHandler))],
            _=>[new(typeof(PackageWorkAvailableV1),ConsumptionFixture.Binding.HandlerType)]
        };
        Assert.Throws<IntegrationConsumptionException>(()=>services.AddSvmMessaging(OutboxFixture.Options(),true,transport));
    }

    private sealed class NeverAuthorizer : IIntegrationWorkAuthorizer
    { public ValueTask<IntegrationWorkAuthority> AuthorizeAsync(IIntegrationEvent message,ModuleOwner owner,bool insideTransaction,CancellationToken token) => throw new NotSupportedException(); }
    private sealed class NeverUnit : IUnitOfWork
    { public Guid? CurrentOperationId => null; public Task<T> ExecuteAsync<T>(Guid operationId,Func<CancellationToken,Task<T>> action,CancellationToken token) => throw new NotSupportedException(); }
    private sealed class NeverTransaction : IIntegrationConsumptionTransaction { public void EnsureActive()=>throw new NotSupportedException(); }
    private sealed class NeverResults : IOperationResultStore
    {
        public Task<StoredOperationResult?> FindAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryAcquireAsync(Guid operationId,CancellationToken token) => throw new NotSupportedException();
        public Task CompleteAsync(OperationResultReference result,CancellationToken token) => throw new NotSupportedException();
    }
}
