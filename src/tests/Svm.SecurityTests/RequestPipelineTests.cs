using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.SecurityTests;

[Trait("Category", "Security")]
public sealed class RequestPipelineTests
{
    private static readonly Guid Software = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Instance = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Work = Guid.Parse("30000000-0000-0000-0000-000000000003");

    public static IEnumerable<object[]> ActorMatrix()
    {
        (Type Type, RequestKind Entry, ActorKind[] Allowed)[] requests =
        [
            (typeof(ManageQuery), RequestKind.Manage, [ActorKind.Human, ActorKind.ManagementSystem]),
            (typeof(HumanQuery), RequestKind.Manage, [ActorKind.Human]),
            (typeof(ClientQuery), RequestKind.Client, [ActorKind.Instance]),
            (typeof(EnrollmentQuery), RequestKind.Enrollment, [ActorKind.EnrollmentGrant]),
            (typeof(RecoveryQuery), RequestKind.Recovery, [ActorKind.RecoveryGrant]),
            (typeof(InternalQuery), RequestKind.Internal, [ActorKind.Service]),
            (typeof(SessionQuery), RequestKind.Session, [ActorKind.Anonymous]),
            (typeof(HumanSessionQuery), RequestKind.Session, [ActorKind.Human])
        ];
        foreach (var (type, entry, allowed) in requests)
            foreach (var actor in Enum.GetValues<ActorKind>())
                yield return [type, entry, actor, allowed.Contains(actor)];
    }

    [Theory]
    [MemberData(nameof(ActorMatrix))]
    public async Task EntryAndActorMatrixIsEnforcedBeforeHandler(Type requestType, RequestKind entry, ActorKind actor, bool allowed)
    {
        var state = NewState(requestType, actor, entry);
        await using var provider = Build(requestType, state);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = requestType == typeof(ManageQuery) ? new ManageQuery(1) : Activator.CreateInstance(requestType)!;
        if (allowed)
        {
            var response = Assert.IsType<ProbeResult>(await sender.Send(request));
            Assert.Equal("handled", response.Value);
            Assert.Equal(1, state.Handled);
            Assert.Contains("authorize", state.Trace);
        }
        else
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => sender.Send(request));
            Assert.Contains(error.StatusCode, new[] { 401, 403 });
            Assert.Equal(0, state.Handled);
            Assert.DoesNotContain("validate", state.Trace);
            Assert.DoesNotContain("authorize", state.Trace);
        }
    }

    [Fact]
    public async Task RealMediatRPipelineRunsValidationThenAuthorizationThenHandler()
    {
        var state = NewState(typeof(ManageQuery));
        await using var provider = Build(typeof(ManageQuery), state);
        await using var scope = provider.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ManageQuery(1), cancellation.Token);
        Assert.Equal(new[] { "context", "validate", "authorize", "handler" }, state.Trace);
        Assert.Equal(cancellation.Token, state.ValidatorToken);
        Assert.Equal(cancellation.Token, state.AuthorizerToken);
        Assert.Equal(cancellation.Token, state.HandlerToken);
    }

    [Fact]
    public async Task InvalidInputDoesNotAuthorizeOrRunHandler()
    {
        var state = NewState(typeof(ManageQuery));
        await using var provider = Build(typeof(ManageQuery), state);
        await using var scope = provider.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new ManageQuery(0)));
        Assert.Equal("VALIDATION_FAILED", error.Code);
        Assert.Equal(422, error.StatusCode);
        Assert.Equal("Count", Assert.Single(error.Errors).Field);
        Assert.Equal(new[] { "context", "validate" }, state.Trace);
        Assert.Equal(0, state.Handled);
    }

    [Fact]
    public async Task MissingContextAndSpoofedEntryCannotReachAuthorizationOrHandler()
    {
        var state = NewState(typeof(ManageQuery));
        state.Context = null;
        await using (var provider = Build(typeof(ManageQuery), state))
        await using (var scope = provider.CreateAsyncScope())
        {
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new ManageQuery(1)));
            Assert.Equal("AUTHENTICATION_REQUIRED", error.Code);
            Assert.Equal(new[] { "context" }, state.Trace);
        }
        state = NewState(typeof(ManageQuery), ActorKind.Human, RequestKind.Internal);
        await using var second = Build(typeof(ManageQuery), state);
        await using var secondScope = second.CreateAsyncScope();
        var mismatch = await Assert.ThrowsAsync<RequestRejectedException>(() => secondScope.ServiceProvider.GetRequiredService<ISender>().Send(new ManageQuery(1)));
        Assert.Equal("PERMISSION_DENIED", mismatch.Code);
        Assert.Equal(new[] { "context" }, state.Trace);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstanceCannotAccessAnotherSoftwareOrInstanceEvenIfProviderReturnsAllow(bool differentSoftware)
    {
        var state = NewState(typeof(ClientQuery), ActorKind.Instance, RequestKind.Client);
        state.Decision = AuthorizationDecision.Allow(AuthorizationTarget.Instance(
            differentSoftware ? Guid.NewGuid() : Software, differentSoftware ? Instance : Guid.NewGuid()));
        await using var provider = Build(typeof(ClientQuery), state);
        await using var scope = provider.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new ClientQuery()));
        Assert.Equal("RESOURCE_NOT_FOUND", error.Code);
        Assert.Equal(0, state.Handled);
    }

    [Fact]
    public async Task EnrollmentAndRecoveryGrantsStayWithinTheirBoundTargets()
    {
        var enrollment = NewState(typeof(EnrollmentQuery), ActorKind.EnrollmentGrant, RequestKind.Enrollment);
        enrollment.Decision = AuthorizationDecision.Allow(AuthorizationTarget.Software(Guid.NewGuid()));
        await AssertRejected<EnrollmentQuery>(enrollment, "PERMISSION_DENIED");
        var recovery = NewState(typeof(RecoveryQuery), ActorKind.RecoveryGrant, RequestKind.Recovery);
        recovery.Decision = AuthorizationDecision.Allow(AuthorizationTarget.Instance(Software, Guid.NewGuid()));
        await AssertRejected<RecoveryQuery>(recovery, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task InternalWorkCannotCrossOwnerOrWorkIdentity()
    {
        var state = NewState(typeof(InternalQuery), ActorKind.Service, RequestKind.Internal);
        state.Decision = AuthorizationDecision.Allow(AuthorizationTarget.Work(ModuleOwner.Tasks, Guid.NewGuid()));
        await AssertRejected<InternalQuery>(state, "PERMISSION_DENIED");
        state = NewState(typeof(InternalQuery), ActorKind.Service, RequestKind.Internal);
        state.Context = new(new CallActor(ActorKind.Service, Guid.NewGuid(), workOwner: ModuleOwner.Packages, workId: Work), RequestKind.Internal, "test");
        await AssertRejected<InternalQuery>(state, "PERMISSION_DENIED");
        Assert.DoesNotContain("authorize", state.Trace);
    }

    [Theory]
    [InlineData(RequestFailure.CredentialInvalid, "CREDENTIAL_INVALID", 401)]
    [InlineData(RequestFailure.PermissionDenied, "PERMISSION_DENIED", 403)]
    [InlineData(RequestFailure.InstanceSuspended, "INSTANCE_SUSPENDED", 403)]
    [InlineData(RequestFailure.DependencyUnavailable, "DEPENDENCY_UNAVAILABLE", 503)]
    public async Task CurrentAuthorizationFailureStopsTheHandler(RequestFailure failure, string code, int status)
    {
        var state = NewState(typeof(ManageQuery));
        state.Decision = AuthorizationDecision.Deny(failure);
        await using var provider = Build(typeof(ManageQuery), state);
        await using var scope = provider.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new ManageQuery(1)));
        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(0, state.Handled);
    }

    [Fact]
    public async Task EachInvocationRechecksAuthorizationWithoutReusingPreviousAllow()
    {
        var state = NewState(typeof(ManageQuery));
        await using var provider = Build(typeof(ManageQuery), state);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await sender.Send(new ManageQuery(1));
        state.Decision = AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => sender.Send(new ManageQuery(1)));
        Assert.Equal("PERMISSION_DENIED", error.Code);
        Assert.Equal(1, state.Handled);
        Assert.Equal(2, state.Trace.Count(t => t == "authorize"));
        Assert.Equal(1, state.Trace.Count(t => t == "context"));
    }

    [Fact]
    public async Task WrongAuthoritativeTargetShapeIsAConfigurationFailure()
    {
        var state = NewState(typeof(ClientQuery), ActorKind.Instance, RequestKind.Client);
        state.Decision = AuthorizationDecision.Allow(AuthorizationTarget.Global());
        await AssertRejected<ClientQuery>(state, "CONFIGURATION_INVALID");
    }

    [Fact]
    public async Task CancellationDuringAuthorizationDoesNotEnterHandler()
    {
        var state = NewState(typeof(ManageQuery));
        using var cancellation = new CancellationTokenSource();
        state.DuringAuthorization = cancellation.Cancel;
        await using var provider = Build(typeof(ManageQuery), state);
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new ManageQuery(1), cancellation.Token));
        Assert.Equal(0, state.Handled);
    }

    [Fact]
    public async Task PreCanceledRequestDoesNotRunValidationAuthorizationOrHandler()
    {
        var state = NewState(typeof(ManageQuery));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var provider = Build(typeof(ManageQuery), state);
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new ManageQuery(1), cancellation.Token));
        Assert.DoesNotContain("validate", state.Trace);
        Assert.DoesNotContain("authorize", state.Trace);
        Assert.Equal(0, state.Handled);
    }

    [Fact]
    public async Task UnregisteredRequestCannotUseAnExistingSender()
    {
        var state = NewState(typeof(ManageQuery));
        await using var provider = Build(typeof(ManageQuery), state);
        await using var scope = provider.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new UnknownQuery()));
        Assert.Equal("CONFIGURATION_INVALID", error.Code);
        Assert.Equal(0, state.Handled);
    }

    private static async Task AssertRejected<T>(State state, string code) where T : IQuery<ProbeResult>, new()
    {
        await using var provider = Build(typeof(T), state);
        await using var scope = provider.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new T()));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, state.Handled);
    }

    private static State NewState(Type requestType, ActorKind actor = ActorKind.Human, RequestKind entry = RequestKind.Manage)
    {
        var identity = new CallActor(actor, actor == ActorKind.Anonymous ? null : Guid.NewGuid(),
            actor is ActorKind.Instance or ActorKind.EnrollmentGrant or ActorKind.RecoveryGrant ? Software : null,
            actor is ActorKind.Instance or ActorKind.RecoveryGrant ? Instance : null,
            actor == ActorKind.Service ? ModuleOwner.Tasks : null, actor == ActorKind.Service ? Work : null);
        var target = requestType == typeof(ClientQuery) || requestType == typeof(RecoveryQuery) ? AuthorizationTarget.Instance(Software, Instance)
            : requestType == typeof(InternalQuery) ? AuthorizationTarget.Work(ModuleOwner.Tasks, Work)
            : requestType == typeof(SessionQuery) || requestType == typeof(HumanSessionQuery) ? AuthorizationTarget.Global()
            : AuthorizationTarget.Software(Software);
        return new State { Context = new(identity, entry, "test-correlation"), Decision = AuthorizationDecision.Allow(target) };
    }

    private static ServiceProvider Build(Type requestType, State state)
    {
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddScoped<ITrustedCallContextSource, Source>();
        services.AddScoped<IRequestAuthorizer, Authorizer>();
        services.AddSvmRequestPipeline([new RequestBinding(requestType, typeof(ProbeHandler<>).MakeGenericType(requestType),
            requestType == typeof(ManageQuery) ? [typeof(ProbeValidator)] : [])]);
        services.ValidateSvmFoundation();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public sealed class State
    {
        public CallContextSnapshot? Context;
        public required AuthorizationDecision Decision;
        public List<string> Trace { get; } = [];
        public int Handled;
        public CancellationToken ValidatorToken;
        public CancellationToken AuthorizerToken;
        public CancellationToken HandlerToken;
        public Action? DuringAuthorization;
    }
    public sealed class Source(State state) : ITrustedCallContextSource
    {
        public CallContextSnapshot? GetCurrent() { state.Trace.Add("context"); return state.Context; }
    }
    public sealed class Authorizer(State state) : IRequestAuthorizer
    {
        public ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken)
        {
            state.Trace.Add("authorize");
            state.AuthorizerToken = cancellationToken;
            state.DuringAuthorization?.Invoke();
            return ValueTask.FromResult(state.Decision);
        }
    }
    public sealed class ProbeValidator : AbstractValidator<ManageQuery>
    {
        public ProbeValidator(State state)
        {
            RuleFor(r => r.Count).MustAsync(async (value, cancellation) =>
            {
                state.Trace.Add("validate"); state.ValidatorToken = cancellation;
                await Task.Yield();
                return value > 0;
            }).WithErrorCode("VALIDATION_FAILED");
        }
    }
    public sealed class ProbeHandler<T>(State state) : IRequestHandler<T, ProbeResult> where T : IQuery<ProbeResult>
    {
        public Task<ProbeResult> Handle(T request, CancellationToken cancellationToken)
        {
            state.Trace.Add("handler"); state.Handled++; state.HandlerToken = cancellationToken;
            return Task.FromResult(new ProbeResult("handled"));
        }
    }
    public sealed record ProbeResult(string Value);
    public sealed record UnknownQuery : IQuery<ProbeResult>;

    [RequestPolicy("fixture.manage", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Software,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, ActorKind.ManagementSystem, Permission = "software.read")]
    public sealed record ManageQuery(int Count) : IQuery<ProbeResult>;
    [RequestPolicy("fixture.human", ModuleOwner.Identity, RequestKind.Manage, RequestScope.Software,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "software.read", ValidationReason = "Test-only actor matrix, no request fields.")]
    public sealed record HumanQuery : IQuery<ProbeResult>;
    [RequestPolicy("fixture.client", ModuleOwner.Instances, RequestKind.Client, RequestScope.Instance,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Instance,
        Permission = "instance.read", ValidationReason = "Test-only actor matrix, no request fields.")]
    public sealed record ClientQuery : IQuery<ProbeResult>;
    [RequestPolicy("fixture.enrollment", ModuleOwner.Identity, RequestKind.Enrollment, RequestScope.Software,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.EnrollmentGrant,
        Permission = "grant.read", ValidationReason = "Test-only actor matrix, no request fields.")]
    public sealed record EnrollmentQuery : IQuery<ProbeResult>;
    [RequestPolicy("fixture.recovery", ModuleOwner.Identity, RequestKind.Recovery, RequestScope.Instance,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.RecoveryGrant,
        Permission = "grant.read", ValidationReason = "Test-only actor matrix, no request fields.")]
    public sealed record RecoveryQuery : IQuery<ProbeResult>;
    [RequestPolicy("fixture.internal", ModuleOwner.Tasks, RequestKind.Internal, RequestScope.InternalWork,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Service,
        Permission = "work.read", ValidationReason = "Test-only actor matrix, no request fields.")]
    public sealed record InternalQuery : IQuery<ProbeResult>;
    [RequestPolicy("fixture.session", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Anonymous,
        ValidationReason = "Test-only anonymous allowlist, no request fields.")]
    public sealed record SessionQuery : IQuery<ProbeResult>;
    [RequestPolicy("fixture.human-session", ModuleOwner.Identity, RequestKind.Session, RequestScope.Global,
        TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.ExplicitlyNone, ActorKind.Human,
        Permission = "session.read", ValidationReason = "Test-only actor matrix, no request fields.")]
    public sealed record HumanSessionQuery : IQuery<ProbeResult>;
}
