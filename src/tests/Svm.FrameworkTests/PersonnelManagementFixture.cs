using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Application;
using Svm.AuditService;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.IdentityService;
using Svm.Security;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;

namespace Svm.FrameworkTests;

internal sealed class PersonnelManagementFixture(PersonnelDatabase database, SessionProof proof) : IAsyncDisposable
{
    internal PersonnelDatabase Personnel { get; } = database;
    internal SessionProof Proof { get; } = proof;
    internal static async Task<PersonnelManagementFixture> CreateAsync()
    {
        var db = await PersonnelDatabase.CreateAsync();
        try
        {
            var login = (await db.LoginAsync()).Grant!;
            var changed = await db.SendAsync(new ChangePasswordCommand(db.Password, Guid.NewGuid().ToString("N")), login.Proof);
            return new(db, changed.Grant!.Proof);
        }
        catch { await db.DisposeAsync(); throw; }
    }
    internal ServiceProvider Provider(SessionProof? proof = null, IInterceptor? interceptor = null, bool auditFailure = false)
    {
        var services = new ServiceCollection();
        services.AddSvmPersonnelManagementApplication();
        services.AddSvmPostgres(Personnel.Database.WriterConnection).AddSvmReadPersistence(Personnel.Database.ReaderConnection).AddSvmUserQueries();
        services.AddSvmPersonnel().AddSvmPersonnelAdministration().AddSvmAudit().AddSvmPersonnelCrypto(Personnel.Policy);
        services.AddSingleton(new PersonnelManagementOptions());
        services.AddScoped<ITrustedCallContextSource>(_ => new Context(proof ?? Proof));
        services.AddScoped<ISessionProofSource>(_ => new Context(proof ?? Proof));
        if (interceptor is not null) services.AddSingleton(interceptor);
        if (auditFailure) services.Replace(ServiceDescriptor.Scoped<IAuditWriter, FailingAudit>());
        services.ValidateSvmFoundation();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
    internal async Task<OperationResult<UserView>> SendAsync(ICommand<UserView> request, SessionProof? proof = null, IInterceptor? interceptor = null, bool auditFailure = false, CancellationToken token = default)
    {
        await using var provider = Provider(proof, interceptor, auditFailure);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, token);
    }
    internal async Task<UserView> UserAsync(Guid id)
    {
        await using var provider = Provider(); await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetUserQuery(id));
    }
    internal async Task<UserPage> PageAsync(UserListInput input)
    {
        await using var provider = Provider(); await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ListUsersQuery(input));
    }
    internal Task<OperationResult<UserView>> CreateUserAsync(string employeeNo = "TEST-USER") =>
        SendAsync(new CreateUserCommand(Guid.NewGuid(), employeeNo, "测试人员", Guid.NewGuid().ToString("N")));
    public ValueTask DisposeAsync() => Personnel.DisposeAsync();
    private sealed class Context(SessionProof proof) : ITrustedCallContextSource, ISessionProofSource
    {
        public SessionProof Proof => proof;
        public string SourceAddress => "personnel-management-fixture";
        public CallContextSnapshot GetCurrent() => new(new CallActor(ActorKind.Human, proof.SubjectId), RequestKind.Manage, Guid.NewGuid().ToString("N"));
    }
    private sealed class FailingAudit : IAuditWriter
    {
        public void Append(AuditFact fact) => throw new InvalidOperationException("fixture audit failure");
    }
}
