using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Messaging.V1;

namespace Svm.Worker.Packages;

internal sealed class PackageWorkerIdentity(PackageFileOptions options) : IPackageServiceIdentity, ITrustedCallContextSource
{
    private static readonly AsyncLocal<(Guid Work, Guid Software)?> Current = new();
    public Guid SubjectId => options.ServiceSubjectId;
    public string NodeId => options.NodeId;
    public string Role => "Executor";
    public CallContextSnapshot? GetCurrent() => Current.Value is { } x ?
        new(new CallActor(ActorKind.Service, SubjectId, x.Software, workOwner: ModuleOwner.Packages, workId: x.Work), RequestKind.Internal, x.Work.ToString("D")) : null;
    internal static IDisposable Enter(Guid work, Guid software)
    { var previous = Current.Value; Current.Value = (work, software); return new Exit(() => Current.Value = previous); }
    private sealed class Exit(Action exit) : IDisposable { public void Dispose() => exit(); }
}
internal sealed class PackageWorkAuthorizer(PackageFileOptions files, SiteCatalogOptions site, IPackages packages,
    IReleases releases, ISoftwareCatalog software, IPersonnelWorkAuthorization personnel) : IIntegrationWorkAuthorizer
{
    public async ValueTask<IntegrationWorkAuthority> AuthorizeAsync(IIntegrationEvent message, ModuleOwner owner, bool protect, CancellationToken token)
    {
        if (owner != ModuleOwner.Packages || message is not PackageWorkAvailableV1 p || p.WorkKind == PackageWorkKind.Cleanup)
            throw new IntegrationConsumptionException(ConsumptionFailure.InvalidOwnership);
        var w = await packages.AuthorityAsync(p.WorkId, false, token);
        if (w.Kind != "Repair" && !await personnel.HasPermissionAsync(w.InitiatorId, w.SoftwareId, "release.upload", protect, token))
            throw new IntegrationConsumptionException(ConsumptionFailure.PermissionDenied);
        if (!await software.ExistsAsync(w.SoftwareId, protect, token) || (await releases.GetAsync(w.ReleaseId, protect, token)).State == "Disabled")
            throw new IntegrationConsumptionException(ConsumptionFailure.PermissionDenied);
        w = await packages.AuthorityAsync(p.WorkId, protect, token);
        if (!await packages.HasDispatchAsync(w.WorkId, p.DispatchSequence, p.EventId, token)) throw new IntegrationConsumptionException(ConsumptionFailure.Conflict);
        return new(new CallActor(ActorKind.Service, files.ServiceSubjectId, w.SoftwareId, workOwner: ModuleOwner.Packages, workId: w.WorkId), owner,
            site.Require().SiteId, w.SoftwareId, w.WorkId, w.Kind == "Repair" ? 2 : 1, w.DispatchSequence, w.DispatchEventId, p.DispatchSequence, p.EventId);
    }
}
