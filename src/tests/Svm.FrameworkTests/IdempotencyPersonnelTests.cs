using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.AuditService;
using Svm.EntityFrameworkCore;
using Svm.EntityFrameworkCore.Framework;
using Svm.Application;
using Svm.IdentityService;
using Svm.Security;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Idempotency;
using Svm.Services.CrossCutting.Registration;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class IdempotencyPersonnelTests
{
    [Fact]
    public async Task RealSessionAndPermissionAreCheckedBeforeReplayAndOldRevisionDoesNotExecuteAgain()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        var firstSession = (await fixture.LoginAsync()).Grant!;
        var proof = (await fixture.SendAsync(new ChangePasswordCommand(fixture.Password, Guid.NewGuid().ToString("N")), firstSession.Proof)).Grant!.Proof;
        await fixture.ExecuteAsync("CREATE TABLE iam.foundation_probe(id uuid PRIMARY KEY,value integer NOT NULL)");
        await using var provider = Provider(fixture, proof);
        var key = Guid.NewGuid(); var resource = Guid.NewGuid(); var calls = 0;
        var policy = new RequestPolicy(new RequestPolicyAttribute("fixture.personnel.idempotency", ModuleOwner.Identity, RequestKind.Manage,
            RequestScope.Global, TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human) { Permission = "software.create" });
        var data = new OperationRequestData(key, OperationValue.Object(), OperationValue.Object(new OperationField("expectedRevision", OperationValue.Integer(0))));
        async Task<OperationResultReference> Execute()
        {
            await using var scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IdempotencyCoordinator>().ExecuteAsync(new object(), policy, data, async token =>
            {
                calls++;
                // A repeated Handler would fail this unchanged revision, whereas a committed replay must return its original reference.
                if (await fixture.Database.CountAsync(resource) != 0) throw new InvalidOperationException("Fixture revision has advanced.");
                await PersistenceDatabase.InsertAsync(scope.ServiceProvider.GetRequiredService<SvmDbContext>(), resource, 1, token);
                var operation = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CurrentOperationId!.Value;
                scope.ServiceProvider.GetRequiredService<IAuditWriter>().Append(new(operation, proof.SubjectId, "Human", null, null,
                    policy.Operation, resource, "Completed", "Component permission verification", operation.ToString("N")));
                return new(operation, OperationStatus.Completed, resource);
            }, default);
        }
        var original = await Execute();
        await fixture.ExecuteAsync("""
            BEGIN;
            UPDATE iam.subject_guards SET "Revision"="Revision"+1;
            DELETE FROM iam.permissions WHERE "Operation"='software.create';
            COMMIT;
            """);
        Assert.Equal(RequestFailure.PermissionDenied, (await Assert.ThrowsAsync<RequestRejectedException>(Execute)).Failure);
        await fixture.ExecuteAsync($"""
            BEGIN;
            UPDATE iam.subject_guards SET "Revision"="Revision"+1;
            INSERT INTO iam.permissions ("Id","SubjectId","SoftwareId","Operation") VALUES ('{Guid.NewGuid()}','{proof.SubjectId}',NULL,'software.create');
            COMMIT;
            """);
        Assert.Equal(original, await Execute()); Assert.Equal(1, calls);
        await fixture.SendAsync(new LogoutCommand(), proof);
        Assert.Equal(RequestFailure.AuthenticationRequired, (await Assert.ThrowsAsync<RequestRejectedException>(Execute)).Failure);
        Assert.Equal(1, calls); Assert.Equal(1, await fixture.CountAsync("iam.operation_results"));
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(fixture.Database.ReaderConnection,
            "SELECT count(*) FROM aud.events WHERE \"Operation\"=@operation", new NpgsqlParameter("operation", policy.Operation)));
    }

    private static ServiceProvider Provider(PersonnelDatabase fixture, SessionProof proof)
    {
        var services = new ServiceCollection();
        services.AddSvmSessionApplication();
        services.AddSvmPostgres(fixture.Database.WriterConnection).AddSvmPersonnel().AddSvmAudit().AddSvmPersonnelCrypto(fixture.Policy);
        services.AddScoped<ITrustedCallContextSource>(_ => new Context(proof));
        services.AddScoped<ISessionProofSource>(_ => new Context(proof));
        services.ValidateSvmFoundation();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
    private sealed class Context(SessionProof proof) : ITrustedCallContextSource, ISessionProofSource
    {
        public SessionProof Proof => proof;
        public string SourceAddress => "idempotency-component-test";
        public CallContextSnapshot GetCurrent() => new(new(ActorKind.Human, proof.SubjectId), RequestKind.Manage, Guid.NewGuid().ToString("N"));
    }
}
