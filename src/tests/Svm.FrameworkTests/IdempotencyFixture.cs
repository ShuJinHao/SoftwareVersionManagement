using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.AuditService;
using Svm.EntityFrameworkCore;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Idempotency;
using Svm.Services.CrossCutting.Registration;
using Svm.Services.CrossCutting.DomainEvents;

namespace Svm.FrameworkTests;

/// <summary>Calls component internals directly; no test Command or production activation switch is registered.</summary>
internal sealed class IdempotencyFixture(PersistenceDatabase database)
{
    internal readonly string Operation = "fixture.idempotency." + Guid.NewGuid().ToString("N");
    internal CallActor Actor = new(ActorKind.Human, Guid.NewGuid());
    internal bool Allowed = true;
    internal int Calls;
    internal readonly System.Collections.Concurrent.ConcurrentBag<Guid> Resources = [];

    internal ServiceProvider Provider(params IInterceptor[] interceptors)
        => ProviderWithDomainEvents([], new DomainEventOptions(), _ => { }, interceptors);

    internal ServiceProvider ProviderWithDomainEvents(IReadOnlyList<DomainEventBinding> domainEvents, DomainEventOptions options,
        Action<IServiceCollection> configure, params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddSvmRequestPipeline([], domainEvents, options).AddSvmPostgres(database.WriterConnection).AddSvmAudit();
        services.AddScoped<ITrustedCallContextSource>(_ => new Source(Actor));
        services.AddScoped<IRequestAuthorizer>(_ => new Authorizer(this));
        foreach (var interceptor in interceptors) services.AddSingleton(interceptor);
        configure(services);
        services.ValidateSvmFoundation();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
    internal RequestPolicy Policy(ModuleOwner owner = ModuleOwner.Identity) => new(new RequestPolicyAttribute(
        Operation, owner, RequestKind.Manage, RequestScope.Global, TransactionMode.DatabaseAtomic,
        IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, ActorKind.ManagementSystem) { Permission = "software.create" });

    internal async Task<OperationResultReference> ExecuteAsync(ServiceProvider provider, Guid key, OperationValue? body = null,
        ModuleOwner owner = ModuleOwner.Identity, Func<AsyncServiceScope, CancellationToken, Task<OperationResultReference>>? action = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = provider.CreateAsyncScope();
        var policy = Policy(owner);
        var data = new OperationRequestData(key, OperationValue.Object(), body ?? Body());
        return await scope.ServiceProvider.GetRequiredService<IdempotencyCoordinator>().ExecuteAsync(new object(), policy, data,
            token => action is null ? ApplyAsync(scope, token) : action(scope, token), cancellationToken);
    }

    internal async Task<OperationResultReference> ApplyAsync(AsyncServiceScope scope, CancellationToken token, bool accepted = false)
    {
        Interlocked.Increment(ref Calls);
        var operationId = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CurrentOperationId!.Value;
        var resourceId = Guid.NewGuid(); Resources.Add(resourceId);
        await PersistenceDatabase.InsertAsync(scope.ServiceProvider.GetRequiredService<SvmDbContext>(), resourceId, 1, token);
        scope.ServiceProvider.GetRequiredService<IAuditWriter>().Append(new(operationId, Actor.ActorId, Actor.Kind.ToString(), null, null,
            Operation, resourceId, accepted ? "Accepted" : "Completed", "Component verification", operationId.ToString("N")));
        return new(operationId, accepted ? OperationStatus.Accepted : OperationStatus.Completed, resourceId, accepted ? Guid.NewGuid() : null);
    }

    internal Task<long> RecordsAsync(string schema = "iam")
    {
        if (schema is not ("iam" or "rel" or "pkg" or "ins" or "tsk" or "aud")) throw new ArgumentException("Invalid test schema.");
        return PersistenceDatabase.ScalarAsync<long>(database.ReaderConnection,
            $"SELECT count(*) FROM {schema}.operation_results WHERE \"Operation\"=@operation", new NpgsqlParameter("operation", Operation));
    }
    internal Task<long> AuditsAsync() => PersistenceDatabase.ScalarAsync<long>(database.ReaderConnection,
        "SELECT count(*) FROM aud.events WHERE \"Operation\"=@operation", new NpgsqlParameter("operation", Operation));
    internal async Task<long> ProbeCountAsync()
    {
        long count = 0;
        foreach (var resource in Resources) count += await database.CountAsync(resource);
        return count;
    }

    internal static OperationValue Body() => OperationValue.Object(new("expectedRevision", OperationValue.Integer(0)),
        new("reason", OperationValue.Text("component test")));
    internal sealed class Source(CallActor actor) : ITrustedCallContextSource
    {
        public CallContextSnapshot GetCurrent() => new(actor, RequestKind.Manage, Guid.NewGuid().ToString("N"));
    }
    private sealed class Authorizer(IdempotencyFixture fixture) : IRequestAuthorizer
    {
        public ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(fixture.Allowed ? AuthorizationDecision.Allow(AuthorizationTarget.Global()) : AuthorizationDecision.Deny(RequestFailure.PermissionDenied));
    }
}
