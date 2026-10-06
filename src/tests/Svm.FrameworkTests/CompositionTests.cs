using System.Collections.Concurrent;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Pipeline;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class CompositionTests
{
    [Fact]
    public async Task BackgroundInvocationsHaveIsolatedContextsAndDisposeAsyncScopedServices()
    {
        var services = CreateServices();
        services.ValidateSvmFoundation();
        await using var provider = Build(services);
        var executor = provider.GetRequiredService<ScopedRequestExecutor>();
        var results = await Task.WhenAll(executor.SendAsync(new ScopeQuery(Synchronize: true), default), executor.SendAsync(new ScopeQuery(Synchronize: true), default));
        Assert.NotEqual(results[0], results[1]);
        var state = provider.GetRequiredService<State>();
        Assert.Equal(2, state.Created.Count);
        Assert.Equal(state.Created.Order(), state.Disposed.Order());
        Assert.Equal(2, state.SourceReads);
    }

    [Fact]
    public async Task HandlerFailureStillDisposesTheScope()
    {
        await using var provider = Build(CreateServices().ValidateSvmFoundation());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<ScopedRequestExecutor>().SendAsync(new ScopeQuery(Fail: true), default));
        var state = provider.GetRequiredService<State>();
        Assert.Equal(Assert.Single(state.Created), Assert.Single(state.Disposed));
    }

    [Fact]
    public async Task CancellationReachesHandlerAndDisposesTheScope()
    {
        await using var provider = Build(CreateServices().ValidateSvmFoundation());
        using var cancellation = new CancellationTokenSource();
        var state = provider.GetRequiredService<State>();
        var task = provider.GetRequiredService<ScopedRequestExecutor>().SendAsync(new ScopeQuery(Wait: true), cancellation.Token);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cancellation.Token, state.HandlerToken);
        Assert.Equal(Assert.Single(state.Created), Assert.Single(state.Disposed));
    }

    [Fact]
    public async Task HttpRequestScopesResolveOneImmutableContextEach()
    {
        // These are the same DI scopes the HTTP host uses; HTTP authentication is not implemented yet.
        await using var provider = Build(CreateServices().ValidateSvmFoundation());
        await using (var scope = provider.CreateAsyncScope())
        {
            var first = scope.ServiceProvider.GetRequiredService<ICallContext>();
            var second = scope.ServiceProvider.GetRequiredService<ICallContext>();
            Assert.Same(first, second);
            Assert.Same(first.Current, second.Current);
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ScopeQuery());
            Assert.Equal(first.Current!.CorrelationId, result.ToString("D"));
        }
        var state = provider.GetRequiredService<State>();
        Assert.Equal(1, state.SourceReads);
        Assert.Equal(Assert.Single(state.Created), Assert.Single(state.Disposed));
    }

    [Fact]
    public void SingletonCapturingScopedDependencyFailsContainerValidation()
    {
        var services = CreateServices().ValidateSvmFoundation();
        services.AddSingleton<Captive>();
        Assert.Throws<AggregateException>(() => Build(services));
    }

    [Fact]
    public async Task FactoryCannotResolveScopedServiceFromRoot()
    {
        var services = CreateServices().ValidateSvmFoundation();
        services.AddSingleton<Captive>(p => new Captive(p.GetRequiredService<ScopeResource>()));
        await using var provider = Build(services);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<Captive>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISender>());
    }

    [Theory]
    [InlineData(typeof(ITrustedCallContextSource))]
    [InlineData(typeof(IRequestAuthorizer))]
    public void ActiveRequestsCannotStartWithoutTrustedSourceAndAuthorizer(Type missingPort)
    {
        var services = CreateServices();
        services.RemoveAll(missingPort);
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }

    [Fact]
    public void ConflictingPortAndDuplicateHandlerAreRejected()
    {
        var ports = CreateServices();
        ports.AddScoped<IRequestAuthorizer, Authorizer>();
        Assert.Throws<InvalidOperationException>(() => ports.ValidateSvmFoundation());
        var handlers = CreateServices();
        handlers.AddScoped<IRequestHandler<ScopeQuery, Guid>, ScopeHandler>();
        Assert.Throws<InvalidOperationException>(() => handlers.ValidateSvmFoundation());
    }

    [Fact]
    public void KeyedIdentityPortCannotBecomeAnAlternateAuthorizationPath()
    {
        var services = CreateServices();
        services.AddKeyedScoped<IRequestAuthorizer, Authorizer>("alternate");
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }

    [Fact]
    public void ChangingPipelineOrderOrLifetimeIsRejected()
    {
        var services = CreateServices();
        var first = services.Single(d => d.ImplementationType == typeof(RequestKindBehavior<,>));
        services.Remove(first);
        services.Add(first);
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
        var wrongLifetime = CreateServices();
        wrongLifetime.Replace(ServiceDescriptor.Singleton<IRequestHandler<ScopeQuery, Guid>, ScopeHandler>());
        Assert.Throws<InvalidOperationException>(() => wrongLifetime.ValidateSvmFoundation());
    }

    [Theory]
    [InlineData(typeof(UnclassifiedQuery))]
    [InlineData(typeof(NeedsValidatorQuery))]
    [InlineData(typeof(UnjustifiedQuery))]
    [InlineData(typeof(TransactionalQuery))]
    [InlineData(typeof(InvalidActorQuery))]
    [InlineData(typeof(IdempotentQuery))]
    [InlineData(typeof(StreamQuery))]
    public void IncompleteOrUnsupportedRequestsCannotBeActivated(Type request)
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddSvmRequestPipeline([
            new RequestBinding(request, typeof(EmptyHandler<>).MakeGenericType(request))]));
    }

    [Fact]
    public void CommandsCannotSkipTheirUnimplementedTransactionAndIdempotencyCapabilities()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSvmRequestPipeline([
            new RequestBinding(typeof(NotYetCommand), typeof(NotYetHandler))]));
    }

    [Fact]
    public void OrphanHandlersAndRepeatedFoundationRegistrationAreRejected()
    {
        var services = CreateServices();
        services.AddScoped<IRequestHandler<UnclassifiedQuery, Guid>, EmptyHandler<UnclassifiedQuery>>();
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
        Assert.Throws<InvalidOperationException>(() => CreateServices().AddSvmRequestPipeline([]));
    }

    [Fact]
    public void ExplicitValidationPolicyCannotLoseItsValidatorAtCompositionTime()
    {
        var services = new ServiceCollection();
        services.AddSvmRequestPipeline([new RequestBinding(typeof(NeedsValidatorQuery), typeof(EmptyHandler<NeedsValidatorQuery>), typeof(Validator))]);
        services.AddScoped<ITrustedCallContextSource, Source>();
        services.AddScoped<IRequestAuthorizer, Authorizer>();
        services.RemoveAll<IValidator<NeedsValidatorQuery>>();
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }

    private static IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<State>();
        services.AddScoped<ScopeResource>();
        services.AddScoped<ITrustedCallContextSource, Source>();
        services.AddScoped<IRequestAuthorizer, Authorizer>();
        services.AddSvmRequestPipeline([new RequestBinding(typeof(ScopeQuery), typeof(ScopeHandler))]);
        return services;
    }

    private static ServiceProvider Build(IServiceCollection services) => services.BuildServiceProvider(new ServiceProviderOptions
    {
        ValidateOnBuild = true, ValidateScopes = true
    });

    public sealed class State
    {
        public ConcurrentBag<Guid> Created { get; } = [];
        public ConcurrentBag<Guid> Disposed { get; } = [];
        public int SourceReads;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConcurrentEntered;
        public CancellationToken HandlerToken;
    }

    public sealed class ScopeResource : IAsyncDisposable
    {
        private readonly State _state;
        public ScopeResource(State state) { _state = state; state.Created.Add(Id); }
        public Guid Id { get; } = Guid.NewGuid();
        public ValueTask DisposeAsync() { _state.Disposed.Add(Id); return ValueTask.CompletedTask; }
    }
    public sealed class Source(ScopeResource resource, State state) : ITrustedCallContextSource
    {
        public CallContextSnapshot GetCurrent()
        {
            Interlocked.Increment(ref state.SourceReads);
            return new(new CallActor(ActorKind.Human, Guid.NewGuid()), RequestKind.Manage, resource.Id.ToString("D"));
        }
    }
    public sealed class Authorizer : IRequestAuthorizer
    {
        public ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuthorizationDecision.Allow(AuthorizationTarget.Global()));
    }
    public sealed class Captive(ScopeResource resource) { public ScopeResource Resource { get; } = resource; }

    [RequestPolicy("fixture.scope", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "fixture.read", ValidationReason = "Test-only lifecycle controls, no external input.")]
    public sealed record ScopeQuery(bool Fail = false, bool Wait = false, bool Synchronize = false) : IQuery<Guid>;

    public sealed class ScopeHandler(ScopeResource resource, ICallContext context, State state) : IRequestHandler<ScopeQuery, Guid>
    {
        public async Task<Guid> Handle(ScopeQuery request, CancellationToken cancellationToken)
        {
            Assert.Equal(resource.Id.ToString("D"), context.Current!.CorrelationId);
            state.HandlerToken = cancellationToken;
            if (request.Fail) throw new InvalidOperationException("Injected handler failure.");
            if (request.Synchronize)
            {
                if (Interlocked.Increment(ref state.ConcurrentEntered) == 2) state.BothStarted.TrySetResult();
                await state.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            if (request.Wait)
            {
                state.Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return resource.Id;
        }
    }

    public sealed record UnclassifiedQuery : IQuery<Guid>;
    [RequestPolicy("fixture.validation", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "fixture.read")]
    public sealed record NeedsValidatorQuery : IQuery<Guid>;
    public sealed class Validator : AbstractValidator<NeedsValidatorQuery>;
    [RequestPolicy("fixture.unjustified", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human, Permission = "fixture.read")]
    public sealed record UnjustifiedQuery : IQuery<Guid>;
    [RequestPolicy("fixture.transaction", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
        TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "fixture.read", ValidationReason = "Test fixture.")]
    public sealed record TransactionalQuery : IQuery<Guid>;
    [RequestPolicy("fixture.actor", ModuleOwner.Identity, RequestKind.Client, RequestScope.Instance,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "fixture.read", ValidationReason = "Test fixture.")]
    public sealed record InvalidActorQuery : IQuery<Guid>;
    [RequestPolicy("fixture.idempotency", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
        TransactionMode.ReadOnly, IdempotencyMode.OperationResult, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "fixture.read", ValidationReason = "Test fixture.")]
    public sealed record IdempotentQuery : IQuery<Guid>;
    [RequestPolicy("fixture.stream", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "fixture.read", ValidationReason = "Test fixture.")]
    public sealed record StreamQuery : IQuery<Guid>, IStreamRequest<Guid>;
    [RequestPolicy("fixture.command", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Global,
        TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "fixture.write", ValidationReason = "Test fixture.")]
    public sealed record NotYetCommand : ICommand<Guid>;
    public sealed class NotYetHandler : IRequestHandler<NotYetCommand, OperationResult<Guid>>
    {
        public Task<OperationResult<Guid>> Handle(NotYetCommand request, CancellationToken cancellationToken) => throw new InvalidOperationException("Must never activate.");
    }
    public sealed class EmptyHandler<T> : IRequestHandler<T, Guid> where T : IQuery<Guid>
    {
        public Task<Guid> Handle(T request, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
    }
}
