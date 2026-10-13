using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Messaging.V1;
using Svm.Worker.Packages;

namespace Svm.Worker.Tasks;

internal sealed class TaskWorkerIdentity(PackageFileOptions files, PackageWorkerIdentity packages) : ITaskServiceIdentity, ITrustedCallContextSource
{
    private static readonly AsyncLocal<(Guid Work, Guid Software)?> Current = new();
    public Guid SubjectId => files.ServiceSubjectId;
    public Guid? WorkId => Current.Value?.Work;
    public CallContextSnapshot? GetCurrent() => Current.Value is { } x ? new(new CallActor(ActorKind.Service, SubjectId, x.Software,
        workOwner: ModuleOwner.Tasks, workId: x.Work), RequestKind.Internal, x.Work.ToString("D")) : packages.GetCurrent();
    internal static IDisposable Enter(Guid work, Guid software)
    { var old = Current.Value; Current.Value = (work, software); return new Exit(() => Current.Value = old); }
    private sealed class Exit(Action exit) : IDisposable { public void Dispose() => exit(); }
}
internal sealed class TaskWorkAuthorizer(PackageWorkAuthorizer packages, PackageFileOptions files, SiteCatalogOptions site,
    ITaskWorkflow tasks, ISoftwareCatalog software, IPersonnelWorkAuthorization personnel) : IIntegrationWorkAuthorizer
{
    public async ValueTask<IntegrationWorkAuthority> AuthorizeAsync(IIntegrationEvent message, ModuleOwner owner, bool protect, CancellationToken ct)
    {
        if (owner == ModuleOwner.Packages) return await packages.AuthorizeAsync(message, owner, protect, ct);
        if (owner != ModuleOwner.Tasks || message is not (TaskPreparationAvailableV1 or TaskControlAvailableV1)) throw new IntegrationConsumptionException(ConsumptionFailure.InvalidOwnership);
        var w = await tasks.AuthorityAsync(message.WorkId, false, ct); var kind = w.Kind switch { "TargetSelection" or "Reschedule" => 1, "Deployment" or "Cancel" => 2, _ => 0 };
        if (kind == 0 || message is TaskPreparationAvailableV1 && w.Kind is not ("TargetSelection" or "Deployment") || message is TaskControlAvailableV1 && w.Kind is not ("Reschedule" or "Cancel")) throw new IntegrationConsumptionException(ConsumptionFailure.InvalidOwnership);
        if (!await personnel.HasPermissionAsync(w.InitiatorId, w.SoftwareId, w.Kind is "Reschedule" or "Cancel" ? "deployment.control" : "deployment.create", protect, ct) ||
            !await software.ExistsAsync(w.SoftwareId, protect, ct)) throw new IntegrationConsumptionException(ConsumptionFailure.PermissionDenied);
        w = await tasks.AuthorityAsync(w.WorkId, protect, ct);
        if (!await tasks.HasDispatchAsync(w.WorkId, message.DispatchSequence, message.EventId, ct)) throw new IntegrationConsumptionException(ConsumptionFailure.Conflict);
        return new(new CallActor(ActorKind.Service, files.ServiceSubjectId, w.SoftwareId, workOwner: ModuleOwner.Tasks, workId: w.WorkId), owner,
            site.Require().SiteId, w.SoftwareId, w.WorkId, kind, w.DispatchSequence, w.DispatchEventId, message.DispatchSequence, message.EventId);
    }
}
