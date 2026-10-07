using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Application;
using Svm.AuditService;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.IdentityService;
using Svm.InstanceService;
using Svm.ReleaseService;
using Svm.Security;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;

namespace Svm.FrameworkTests;

internal sealed class SiteCatalogFixture(PersonnelManagementFixture personnel, SiteCatalogOptions options) : IAsyncDisposable
{
    internal PersonnelManagementFixture Personnel { get; } = personnel;
    internal SiteCatalogOptions Options { get; } = options;
    internal SessionProof Proof => Personnel.Proof;
    internal static async Task<SiteCatalogFixture> CreateAsync(bool configured = true)
    {
        var personnel = await PersonnelManagementFixture.CreateAsync();
        try
        {
            var fixture = new SiteCatalogFixture(personnel, configured ? new(Guid.NewGuid(), "夹具厂区", "Asia/Shanghai") : new());
            var user = await fixture.UserAsync(fixture.Proof.SubjectId);
            await fixture.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision,
                [new(null, "identity.manage"), new(null, "software.create"), new(null, "asset.read"), new(null, "asset.manage")], "夹具显式授权"));
            return fixture;
        }
        catch { await personnel.DisposeAsync(); throw; }
    }
    internal ServiceProvider Provider(SessionProof? proof = null, IInterceptor? interceptor = null, bool auditFailure = false, SiteCatalogOptions? options = null, bool grantFailure = false)
    {
        var services = new ServiceCollection(); services.AddSvmSiteCatalogApplication();
        services.AddSvmPostgres(Personnel.Personnel.Database.WriterConnection).AddSvmReadPersistence(Personnel.Personnel.Database.ReaderConnection).AddSvmUserQueries().AddSvmCatalogQueries();
        services.AddSvmPersonnel().AddSvmPersonnelAdministration().AddSvmPersonnelSoftwareAdministration().AddSvmSoftwareCatalog().AddSvmSiteAssets().AddSvmAudit().AddSvmPersonnelCrypto(Personnel.Personnel.Policy);
        services.AddSingleton(new PersonnelManagementOptions()); services.AddSingleton(options ?? Options);
        services.AddScoped<ITrustedCallContextSource>(_ => new Context(proof ?? Proof)); services.AddScoped<ISessionProofSource>(_ => new Context(proof ?? Proof));
        if (interceptor is not null) services.AddSingleton(interceptor);
        if (auditFailure) services.Replace(ServiceDescriptor.Scoped<IAuditWriter, FailingAudit>());
        if (grantFailure) services.Replace(ServiceDescriptor.Scoped<IPersonnelSoftwareAdministration, FailingCreatorGrant>());
        services.ValidateSvmFoundation(); return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
    internal async Task<T> SendAsync<T>(IRequest<T> request, SessionProof? proof = null, IInterceptor? interceptor = null, bool auditFailure = false, CancellationToken token = default, SiteCatalogOptions? options = null, bool grantFailure = false)
    { await using var provider = Provider(proof, interceptor, auditFailure, options, grantFailure); await using var scope = provider.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, token); }
    internal Task<UserView> UserAsync(Guid id) => SendAsync(new GetUserQuery(id));
    internal Task<OperationResult<SoftwareView>> SoftwareAsync(string code = "FIXTURE-SOFTWARE", string category = "UpperComputer") =>
        SendAsync(new CreateSoftwareCommand(Guid.NewGuid(), code, "夹具软件", category, "测试资料"));
    internal async Task<(ProcessView Process, DeviceView Device)> DeviceAsync(string code = "FIXTURE-P", string number = "FIXTURE-D")
    { var process = (await SendAsync(new CreateProcessCommand(Guid.NewGuid(), code, "夹具工序"))).Value; var device = (await SendAsync(new CreateDeviceCommand(Guid.NewGuid(), process.Id, number, "夹具二期设备"))).Value; return (process, device); }
    internal async Task<SessionProof> PersonAsync(IReadOnlyList<PermissionView> permissions)
    {
        var password = Guid.NewGuid().ToString("N"); var employee = "FIXTURE-" + Guid.NewGuid().ToString("N");
        var user = (await SendAsync(new CreateUserCommand(Guid.NewGuid(), employee, "夹具人员", password))).Value;
        await SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), user.Id, user.Revision, permissions, "夹具软件授权"));
        var login = (await Personnel.Personnel.LoginAsync(password, employee)).Grant!;
        return (await Personnel.Personnel.SendAsync(new ChangePasswordCommand(password, Guid.NewGuid().ToString("N")), login.Proof)).Grant!.Proof;
    }
    internal Task<long> CountAsync(string table) => Personnel.Personnel.CountAsync(table);
    public ValueTask DisposeAsync() => Personnel.DisposeAsync();
    private sealed class Context(SessionProof proof) : ITrustedCallContextSource, ISessionProofSource
    { public SessionProof Proof => proof; public string SourceAddress => "site-catalog-fixture"; public CallContextSnapshot GetCurrent() => new(new(ActorKind.Human, proof.SubjectId), RequestKind.Manage, Guid.NewGuid().ToString("N")); }
    private sealed class FailingAudit : IAuditWriter { public void Append(AuditFact fact) => throw new InvalidOperationException("fixture audit failure"); }
    private sealed class FailingCreatorGrant : IPersonnelSoftwareAdministration
    {
        public Task GrantCreatorAsync(Guid id, CancellationToken token) => throw new InvalidOperationException("fixture grant failure");
        public Task<UserView> ReplaceAsync(Guid id, long revision, IReadOnlyList<PermissionView> permissions, CancellationToken token) => throw new InvalidOperationException();
    }
}
