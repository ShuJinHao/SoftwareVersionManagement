using Svm.Core.Releases;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Plc;
namespace Svm.ReleaseService;

internal sealed class PlcPublicationService(IPlcPublicationRepository evidence, IReleaseRepository releases, IUnitOfWork unit, TimeProvider clock) : IPlcPublication
{
    public async Task BindUploadAsync(Guid releaseId, PlcScope scope, CancellationToken token)
    {
        if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        (await releases.GetAsync(releaseId, true, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound)).BindPlcUpload(scope.BindingId, scope.BindingRevision);
    }
    public async Task<PlcScope?> UploadScopeAsync(Guid releaseId, CancellationToken token)
    {
        var r = await releases.GetAsync(releaseId, false, token);
        return r?.PlcUploadBindingId is { } id && r.PlcUploadBindingRevision is { } revision ? new(id, revision) : null;
    }
    public async Task<ReleaseView> PublishAsync(PublishPlcReleaseCommand x, Guid hardwareId, Guid actor, string employeeNo, CancellationToken token)
    {
        if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        var r = await releases.GetAsync(x.ReleaseId, true, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (r.Revision != x.ExpectedRevision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (r.SourceKind != "PLC" || r.State != "Test" || r.PublishedAt is not null) throw new RequestRejectedException(RequestFailure.InvalidState);
        var e = new PlcPublication(Guid.NewGuid(), x.ReleaseId, hardwareId, x.BindingId, x.BindingRevision, x.FieldAt,
            x.Verification, x.Materials, actor, employeeNo, clock.GetUtcNow());
        r.PublishPlc(e.Id, x.PublishReason, x.PublishConclusion, actor, employeeNo, e.RecordedAt); evidence.Add(e);
        return Releases.View(r);
    }
    public async Task<PlcEvidenceView?> GetAsync(Guid releaseId, CancellationToken token)
    {
        var x = await evidence.GetAsync(releaseId, token);
        return x is null ? null : new(x.Id,x.ReleaseId,x.HardwareId,x.BindingId,x.BindingRevision,x.FieldAt,x.Verification,x.Materials,x.ActorId,x.EmployeeNo,x.RecordedAt);
    }
}
