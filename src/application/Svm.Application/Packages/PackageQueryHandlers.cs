using MediatR;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Application.Packages;

internal sealed class ListReleasesQueryHandler(IReleaseQueries queries) : IRequestHandler<ListReleasesQuery, ReleasePage<ReleaseView>>
{ public Task<ReleasePage<ReleaseView>> Handle(ListReleasesQuery x, CancellationToken t) => queries.ListAsync(x.Input, t); }
internal sealed class GetReleaseQueryHandler(IReleaseQueries queries) : IRequestHandler<GetReleaseQuery, ReleaseView>
{ public async Task<ReleaseView> Handle(GetReleaseQuery x, CancellationToken t) => await queries.GetAsync(x.ReleaseId, t) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
internal sealed class GetPackageQueryHandler(IPackageQueries packages) : IRequestHandler<GetPackageQuery, PackageView>
{ public async Task<PackageView> Handle(GetPackageQuery x, CancellationToken t) => await packages.GetAsync(x.PackageId, x.Upload, t) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
internal sealed class GetPackageWorkQueryHandler(IPackageQueries packages) : IRequestHandler<GetPackageWorkQuery, PackageWorkView>
{ public async Task<PackageWorkView> Handle(GetPackageWorkQuery x, CancellationToken t) => await packages.WorkAsync(x.WorkId, t) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); }
internal sealed class GetTestEvidenceQueryHandler(IReleaseQueries queries) : IRequestHandler<GetTestEvidenceQuery, EvidencePage>
{ public Task<EvidencePage> Handle(GetTestEvidenceQuery x, CancellationToken t) => queries.EvidenceAsync(x.ReleaseId, x.PageSize, x.After, t); }
internal sealed class GetDownloadAuditQueryHandler(IReleaseQueries queries) : IRequestHandler<GetDownloadAuditQuery, DownloadAuditPage>
{ public Task<DownloadAuditPage> Handle(GetDownloadAuditQuery x, CancellationToken t) => queries.DownloadsAsync(x.SoftwareId, x.PageSize, x.After, t); }
internal sealed class ListClientReleasesQueryHandler(IReleaseQueries queries) : IRequestHandler<ListClientReleasesQuery, ReleasePage<ClientReleaseView>>
{
    public async Task<ReleasePage<ClientReleaseView>> Handle(ListClientReleasesQuery x, CancellationToken t)
    {
        return await queries.ClientListAsync(x.Input, t);
    }
    internal static ClientReleaseView View(ReleaseView r, PackageView p) => new(r.Id, r.Version, r.State, r.ChangeSummary, r.ChangeReason, p.Id, p.SizeBytes!.Value, p.Sha256!, p.DownloadPath!);
}
internal sealed class GetClientReleaseQueryHandler(IReleaseQueries queries, IPackages packages) : IRequestHandler<GetClientReleaseQuery, ClientReleaseView>
{
    public async Task<ClientReleaseView> Handle(GetClientReleaseQuery x, CancellationToken t)
    {
        var r = await queries.GetAsync(x.ReleaseId, t) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var p = await packages.GetAsync(r.PackageId, false, false, t);
        if (r.State is not ("Test" or "Formal") || !p.DownloadAvailable) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        return ListClientReleasesQueryHandler.View(r, p);
    }
}
internal sealed class GetClientPackageQueryHandler(IPackageQueries packages, IReleases releases) : IRequestHandler<GetClientPackageQuery, PackageView>
{
    public async Task<PackageView> Handle(GetClientPackageQuery x, CancellationToken t)
    { var p = await packages.GetAsync(x.PackageId, false, t) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound); var r = await releases.GetAsync(p.ReleaseId, false, t);
      if (r.State == "Staging") throw new RequestRejectedException(RequestFailure.ResourceNotFound); return p; }
}

internal sealed class InspectReplicaQueryHandler(IPackages packages) : IRequestHandler<InspectReplicaQuery, PackageView>
{ public Task<PackageView> Handle(InspectReplicaQuery x, CancellationToken t) => packages.GetAsync(x.PackageId, false, false, t); }

internal sealed class GetPackageCapabilitiesQueryHandler(PackageLimits limits) : IRequestHandler<GetPackageCapabilitiesQuery,PackageCapabilitiesView>
{ public Task<PackageCapabilitiesView> Handle(GetPackageCapabilitiesQuery x,CancellationToken t) => Task.FromResult(new PackageCapabilitiesView(limits.MaxPackageBytes)); }

internal sealed class GetUploadTargetQueryHandler(IPackages packages) : IRequestHandler<GetUploadTargetQuery,string>
{ public async Task<string> Handle(GetUploadTargetQuery x,CancellationToken t) => (await packages.AuthorityAsync(x.UploadId,false,t)).SourceNode; }

internal sealed class GetDownloadEndQueryHandler(IPackages packages) : IRequestHandler<GetDownloadEndQuery,DownloadEnd?>
{ public Task<DownloadEnd?> Handle(GetDownloadEndQuery x,CancellationToken t) => packages.FindDownloadEndAsync(x.RequestId,x.NodeId,x.WorkerGeneration,t); }
