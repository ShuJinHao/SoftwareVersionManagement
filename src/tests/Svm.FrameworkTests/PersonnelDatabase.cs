using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.AuditService;
using Svm.EntityFrameworkCore;
using Svm.IdentityService;
using Svm.Security;
using Svm.ServiceDefaults;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;

namespace Svm.FrameworkTests;

internal sealed class PersonnelDatabase : IAsyncDisposable
{
    internal PersistenceDatabase Database { get; } = new();
    internal string Password { get; } = Guid.NewGuid().ToString("N") + "initial";
    internal const string EmployeeNo = "TEST-ADMIN";
    internal PersonnelPolicy Policy { get; } = PersonnelConfiguration.LoadFromEnvironment().Policy;
    internal static async Task<PersonnelDatabase> CreateAsync(bool seed = true)
    {
        var fixture = new PersonnelDatabase();
        try
        {
            await fixture.Database.CreateAsync(migrate: true);
            if (seed) await fixture.SeedAsync();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    internal ServiceProvider Provider(bool seed, SessionProof? proof = null, string address = "test-source", IInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        if (seed) services.AddSvmSeedApplication(); else services.AddSvmSessionApplication();
        services.AddSvmPostgres(Database.WriterConnection).AddSvmPersonnel().AddSvmAudit().AddSvmPersonnelCrypto(Policy);
        if (interceptor is not null) services.AddSingleton(interceptor);
        services.AddScoped<ITrustedCallContextSource>(_ => new Context(seed, proof, address));
        services.AddScoped<ISessionProofSource>(_ => new Context(seed, proof, address));
        services.ValidateSvmFoundation();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
    internal async Task<bool> SeedAsync(string? password = null, IInterceptor? interceptor = null)
    {
        await using var provider = Provider(true, interceptor: interceptor);
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            new SeedPersonnelCommand(EmployeeNo, "测试管理员", password ?? Password))).Value.Changed;
    }
    internal async Task<PersonnelMutation> LoginAsync(string? password = null, string employeeNo = EmployeeNo, string address = "test-source", IInterceptor? interceptor = null)
    {
        await using var provider = Provider(false, address: address, interceptor: interceptor);
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand(employeeNo, password ?? Password))).Value;
    }
    internal async Task<PersonnelMutation> SendAsync(ICommand<PersonnelMutation> request, SessionProof proof, IInterceptor? interceptor = null)
    {
        await using var provider = Provider(false, proof, interceptor: interceptor);
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISender>().Send(request)).Value;
    }
    internal async Task<PersonnelView?> AuthenticateAsync(SessionProof proof)
    {
        await using var provider = Provider(false, proof);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPersonnelService>().AuthenticateAsync(proof, false, default);
    }
    internal Task<long> CountAsync(string table) => PersistenceDatabase.ScalarAsync<long>(Database.ReaderConnection, $"SELECT count(*) FROM {table}");
    internal Task ExecuteAsync(string sql) => PersistenceDatabase.ExecuteAsync(Database.MigrationConnection, sql);
    public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    private sealed class Context(bool seed, SessionProof? proof, string address) : ITrustedCallContextSource, ISessionProofSource
    {
        public SessionProof? Proof => proof;
        public string SourceAddress => address;
        private readonly Guid _work = Guid.NewGuid();
        public CallContextSnapshot GetCurrent() => new(seed
            ? new CallActor(ActorKind.Service, Guid.Parse("b0b90b26-1842-45a4-823d-e8c321f259e2"), workOwner: ModuleOwner.Identity, workId: _work)
            : proof is null ? new CallActor(ActorKind.Anonymous) : new CallActor(ActorKind.Human, proof.SubjectId),
            seed ? RequestKind.Internal : RequestKind.Session, _work.ToString("N"));
    }
}
