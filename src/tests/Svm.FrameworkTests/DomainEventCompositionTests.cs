using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.DomainEvents;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class DomainEventCompositionTests
{
    [Fact]
    public async Task EmptyDirectoryUsesScopedDispatchAndImmutableDefaultLimit()
    {
        var services = new ServiceCollection().AddSvmDomainEvents([]).ValidateSvmDomainEvents();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var dispatcher = first.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
        Assert.Same(dispatcher, first.ServiceProvider.GetRequiredService<IDomainEventDispatcher>());
        Assert.NotSame(dispatcher, second.ServiceProvider.GetRequiredService<IDomainEventDispatcher>());
        Assert.Equal(1000, provider.GetRequiredService<DomainEventOptions>().MaximumEventsPerTransaction);
        Assert.Throws<InvalidOperationException>(() => services.AddSvmDomainEvents([]));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("handler-owner")]
    [InlineData("missing-handler")]
    [InlineData("duplicate-event")]
    [InlineData("duplicate-handler")]
    [InlineData("duplicate-order")]
    [InlineData("wrong-event-contract")]
    public void InvalidDirectoriesCannotBeActivated(string error)
    {
        var eventType = DomainEventTestTypes.Event;
        var handler = DomainEventTestTypes.FirstHandler;
        IReadOnlyList<DomainEventBinding> bindings = error switch
        {
            "owner" => [new(ModuleOwner.Instances, eventType, new DomainEventHandlerBinding(handler, 1))],
            "handler-owner" => [new(ModuleOwner.Identity, eventType, new DomainEventHandlerBinding(typeof(DomainEventTestHandler<>).MakeGenericType(eventType), 1))],
            "missing-handler" => [new(ModuleOwner.Identity, eventType)],
            "duplicate-event" => [DomainEventFixture.Binding(), DomainEventFixture.Binding()],
            "duplicate-handler" => [new(ModuleOwner.Identity, eventType, new(handler, 1), new(handler, 2))],
            "duplicate-order" => [new(ModuleOwner.Identity, eventType, new(handler, 1), new(DomainEventTestTypes.SecondHandler, 1))],
            "wrong-event-contract" => [new(ModuleOwner.Identity, DomainEventTestTypes.OtherEvent, new DomainEventHandlerBinding(handler, 1))],
            _ => throw new ArgumentException("Unknown fixture.")
        };
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSvmDomainEvents(bindings));
    }

    [Theory]
    [InlineData("singleton-handler")]
    [InlineData("transient-handler")]
    [InlineData("duplicate-handler")]
    [InlineData("missing-handler")]
    [InlineData("singleton-dispatcher")]
    [InlineData("keyed-handler")]
    [InlineData("typed-bypass")]
    [InlineData("orphan-handler")]
    [InlineData("missing-subscription")]
    public void RegistrationChangesCannotBypassTheDirectoryOrScope(string change)
    {
        var services = new ServiceCollection().AddSvmDomainEvents([DomainEventFixture.Binding()]);
        var handler = DomainEventTestTypes.FirstHandler;
        switch (change)
        {
            case "singleton-handler": services.Replace(ServiceDescriptor.Singleton(handler, handler)); break;
            case "transient-handler": services.Replace(ServiceDescriptor.Transient(handler, handler)); break;
            case "duplicate-handler": services.AddScoped(handler); break;
            case "missing-handler": services.RemoveAll(handler); break;
            case "singleton-dispatcher": services.Replace(ServiceDescriptor.Singleton<IDomainEventDispatcher, DomainEventDispatcher>()); break;
            case "keyed-handler": services.AddKeyedScoped(handler, "other", handler); break;
            case "typed-bypass": services.AddScoped(typeof(IDomainEventHandler<>).MakeGenericType(DomainEventTestTypes.Event), handler); break;
            case "orphan-handler": services.AddScoped(DomainEventTestTypes.SecondHandler); break;
            case "missing-subscription": services.RemoveAll<IDomainEventSubscription>(); break;
        }
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmDomainEvents());
    }

    [Fact]
    public async Task SubscriberOrderIsExplicitAndTheDirectoryCopiesItsInput()
    {
        var state = new DomainEventTestState();
        DomainEventHandlerBinding[] handlers = [new(DomainEventTestTypes.SecondHandler, 20), new(DomainEventTestTypes.FirstHandler, 10)];
        var binding = new DomainEventBinding(ModuleOwner.Identity, DomainEventTestTypes.Event, handlers);
        handlers[0] = new(typeof(object), -1);
        var services = new ServiceCollection().AddSvmDomainEvents([binding]);
        services.AddSingleton(state);
        services.ValidateSvmDomainEvents();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>().DispatchAsync(DomainEventFixture.Aggregate(), DomainEventFixture.Event(1), default);
        Assert.Equal(["First:1", "Second:1"], state.Calls);
        Assert.Throws<NotSupportedException>(() => ((IList<DomainEventHandlerBinding>)binding.Handlers).Clear());
    }

    [Fact]
    public async Task NextSubscriberWaitsForThePreviousSubscriberToFinish()
    {
        var state = new DomainEventTestState();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.OnHandle = async (handler, _, token) =>
        {
            if (handler != "First") return;
            entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        };
        var services = new ServiceCollection().AddSvmDomainEvents([DomainEventFixture.Binding(multiple: true)]);
        services.AddSingleton(state);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>().DispatchAsync(DomainEventFixture.Aggregate(), DomainEventFixture.Event(1), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(["First:1"], state.Calls); Assert.False(dispatch.IsCompleted);
        release.SetResult(); await dispatch; Assert.Equal(["First:1", "Second:1"], state.Calls);
    }

    [Fact]
    public async Task DispatcherRejectsInvalidEventIdentityAndUtcTime()
    {
        var services = new ServiceCollection().AddSvmDomainEvents([DomainEventFixture.Binding()]);
        services.AddSingleton(new DomainEventTestState());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
        foreach (var domainEvent in new[] { DomainEventFixture.Event(1, Guid.Empty), DomainEventFixture.Event(1, occurredAt: default(DateTimeOffset)),
            DomainEventFixture.Event(1, occurredAt: DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8))) })
            Assert.Equal(DomainEventFailure.InvalidEvent, (await Assert.ThrowsAsync<DomainEventDispatchException>(() =>
                dispatcher.DispatchAsync(DomainEventFixture.Aggregate(), domainEvent, default))).Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ProcessingLimitMustBePositive(int limit) => Assert.Throws<ArgumentOutOfRangeException>(() => new DomainEventOptions(limit));
}
