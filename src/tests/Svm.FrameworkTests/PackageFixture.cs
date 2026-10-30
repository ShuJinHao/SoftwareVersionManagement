using MediatR;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Application;
using Svm.AuditService;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.EventBus;
using Svm.IdentityService;
using Svm.InstanceService;
using Svm.PackageService;
using Svm.ReleaseService;
using Svm.Security;
using Svm.Services.Contracts.Audit;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;
using Svm.Services.CrossCutting.Registration;
namespace Svm.FrameworkTests;
internal sealed class PackageFixture(InstanceFixture instances) : IAsyncDisposable
{
    internal InstanceFixture Instances { get; } = instances;
    internal PersistenceDatabase Database => Instances.Site.Personnel.Personnel.Database;
    internal Guid SoftwareId => Instances.SoftwareId;
    internal static PackageLimits Limits => new(32 * 1024 * 1024, 5);
    internal static PackageExecutionOptions Execution => new(15, 1, 5, 20);
    internal static PackageNodeCatalog Nodes => new(["node-a", "node-b"]);
    internal static PackageInput Input => new("fixture.zip", 123, new string('a', 64));
    internal static async Task<PackageFixture> CreateAsync()
    {
        var f = new PackageFixture(await InstanceFixture.CreateAsync());
        try { var u = await f.Instances.Site.UserAsync(f.Instances.Site.Proof.SubjectId); await f.Instances.Site.SendAsync(new ReplaceUserPermissionsCommand(Guid.NewGuid(), u.Id, u.Revision, u.Permissions.Concat(new[] { "release.upload", "release.disable", "audit.read" }.Select(o => new PermissionView(f.SoftwareId, o))).ToArray(), "版本包夹具显式授权")); return f; }
        catch { await f.DisposeAsync(); throw; }
    }
    internal ServiceProvider Provider(SessionProof? person = null, AccessProof? access = null, Guid? instance = null, Guid? work = null, string role = "Executor", string node = "node-a", IInterceptor? interceptor = null, bool auditFailure = false, GitLabImportOptions? gitLab = null, bool importer = false, ModuleOwner importOwner = ModuleOwner.Releases, GitLabBinding? importBinding = null, Guid? plcSoftware = null)
    {
        var s = new ServiceCollection(); if (importer) s.AddSvmPackageWorkerApplication(); else s.AddSvmPackageApplication();
        s.AddSvmPostgres(Database.WriterConnection).AddSvmReadPersistence(Database.ReaderConnection).AddSvmUserQueries().AddSvmCatalogQueries().AddSvmReleaseQueries();
        s.AddSvmPersonnel().AddSvmPersonnelAdministration().AddSvmPersonnelSoftwareAdministration().AddSvmPersonnelWorkAuthorization().AddSvmSoftwareCatalog().AddSvmSiteAssets().AddSvmAudit().AddSvmPersonnelCrypto(Instances.Site.Personnel.Personnel.Policy).AddSvmInstanceAccess().AddSvmManagedInstances().AddSvmReleases().AddSvmPackages();
        s.AddSingleton(new Svm.Services.Contracts.Plc.PlcOptions(86400)); s.AddSingleton(new PersonnelManagementOptions()); s.AddSingleton(Instances.Site.Options); s.AddSingleton(InstanceFixture.Limits); s.AddSingleton<TimeProvider>(Instances.Clock); s.AddSingleton(Limits); s.AddSingleton(Execution); s.AddSingleton(Nodes);
        var context = new Context(person ?? Instances.Site.Proof, access, plcSoftware ?? SoftwareId, instance, work, role, node, importer ? importBinding ?? gitLab!.Bindings.Single() : null, importOwner);
        if (gitLab is not null) s.AddSingleton(gitLab); s.AddScoped<IGitLabServiceIdentity>(_ => context);
        s.AddScoped<ITrustedCallContextSource>(_ => context); s.AddScoped<ISessionProofSource>(_ => context); s.AddScoped<IAccessProofSource>(_ => context); s.AddScoped<IPackageServiceIdentity>(_ => context); s.AddScoped<IPackageDownloadProof>(_ => context); s.AddScoped<IPackageFiles, NoFiles>();
        s.AddSvmMessaging(OutboxFixture.Options(Instances.Site.Options.SiteId), delivery: false);
        if (interceptor is not null) s.AddSingleton(interceptor); if (auditFailure) s.Replace(ServiceDescriptor.Scoped<IAuditWriter, FailingAudit>());
        s.ValidateSvmFoundation(); return s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
    internal async Task<T> SendAsync<T>(IRequest<T> request, SessionProof? person = null, AccessProof? access = null, Guid? instance = null, Guid? work = null, string role = "Executor", string node = "node-a", IInterceptor? interceptor = null, bool auditFailure = false, CancellationToken token = default)
    { await using var p = Provider(person, access, instance, work, role, node, interceptor, auditFailure); await using var scope = p.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, token); }
    internal CreateReleaseCommand Create(string level = "Patch", string? expected = null, Guid? key = null) => new(key ?? Guid.NewGuid(), SoftwareId, level, "夹具更新说明", "夹具创建原因", expected, Input);
    internal Task<long> Count(string table) => PersistenceDatabase.ScalarAsync<long>(Database.ReaderConnection, "SELECT count(*) FROM " + table);
    internal async Task<PackageWorkAuthority> Received(ReleaseUploadResult r)
    {
        var receipt = (await SendAsync(new BeginUploadCommand(r.UploadId, Guid.NewGuid(), "node-a"))).Value; await SendAsync(new FinishUploadCommand(receipt, Input.SizeBytes, Input.Sha256));
        await using var p = Provider(); await using var scope = p.CreateAsyncScope(); var packages = scope.ServiceProvider.GetRequiredService<IPackages>(); var w = await packages.AuthorityAsync(r.UploadId, false, default);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async ct => { await packages.AcceptAsync(w.WorkId, w.DispatchSequence, w.DispatchEventId, ct); return true; }, default); return w;
    }
    internal async Task<PackageLease> Lease(ReleaseUploadResult r, string node = "node-a") { await Received(r); return (await SendAsync(new ClaimPackageWorkCommand(r.UploadId, node, Guid.NewGuid()), work: r.UploadId, node: node)).Value!; }
    internal IReadOnlyList<ReplicaFact> Healthy => Nodes.Nodes.Select(n => new ReplicaFact(n, "Healthy", Instances.Clock.GetUtcNow())).ToArray();
    public ValueTask DisposeAsync() => Instances.DisposeAsync();
    private sealed class FailingAudit : IAuditWriter { public void Append(AuditFact fact) => throw new IOException("package fixture audit failure"); }
    private sealed class Context(SessionProof human, AccessProof? access, Guid software, Guid? instance, Guid? work, string role, string node, GitLabBinding? binding = null, ModuleOwner importOwner = ModuleOwner.Releases) : IGitLabServiceIdentity, ITrustedCallContextSource, ISessionProofSource, IAccessProofSource, IPackageServiceIdentity, IPackageDownloadProof
    {
        private static readonly Guid Service = Guid.Parse("86c64021-789c-40a8-963d-6f3245174138");
        public Guid? BindingId => binding?.Id;
        public long SourceRevision => binding?.SourceRevision ?? 1;
        public Guid SubjectId => Service; public string NodeId => node; public string Role => role; public SessionProof? Person => access is null ? human : null; public AccessProof? Instance => access;
        SessionProof? ISessionProofSource.Proof => access is null ? human : null; AccessProof? IAccessProofSource.Proof => access; public string SourceAddress => "package-fixture";
        public CallContextSnapshot GetCurrent() => binding is not null ? new(new(ActorKind.Service, binding.SubjectId, binding.SoftwareId, workOwner: importOwner, workId: work ?? binding.Id), RequestKind.Internal, Guid.NewGuid().ToString("N")) : work is not null ? new(new(ActorKind.Service, Service, workOwner: ModuleOwner.Packages, workId: work), RequestKind.Internal, Guid.NewGuid().ToString("N")) : access is not null ? new(new(access.Kind, access.Kind == ActorKind.PlcUpload ? access.Id : instance, software, access.Kind == ActorKind.PlcUpload ? null : instance), access.Kind == ActorKind.PlcUpload ? RequestKind.PlcUpload : RequestKind.Client, Guid.NewGuid().ToString("N")) : new(new(ActorKind.Human, human.SubjectId), RequestKind.Manage, Guid.NewGuid().ToString("N"));
    }
    // Transaction tests never invoke file effects. Separate fixtures use the real adapter.
    private sealed class NoFiles : IPackageFiles
    {
        public string NodeId => "node-a";
        public Task<IAsyncDisposable> LockUploadAsync(Guid id, CancellationToken t) => throw new NotSupportedException();
        public Task<(long Size, string Sha256)> ReceiveAsync(UploadReceipt r, Stream s, long? l, Func<CancellationToken, Task> v, CancellationToken t) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReplicaFact>> PrepareReplicasAsync(PackageLease l, Func<CancellationToken, Task> r, CancellationToken t) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReplicaFact>> InspectAsync(Guid id, long s, string h, CancellationToken t) => throw new NotSupportedException();
        public Task<ReplicaFact> InspectNodeAsync(string node, Guid id, long s, string h, CancellationToken t) => throw new NotSupportedException();
        public Task<Stream> OpenReplicaAsync(Guid id, CancellationToken t) => throw new NotSupportedException();
        public Task<ReplicaFact> InspectLocalAsync(Guid id, long s, string h, CancellationToken t) => throw new NotSupportedException();
        public Task ReceiveReplicaAsync(PackageWorkAuthority w, Stream s, Func<CancellationToken, Task> v, CancellationToken t) => throw new NotSupportedException();
        public Task<Stream> OpenSourceAsync(PackageWorkAuthority w, CancellationToken t) => throw new NotSupportedException();
    }
}
