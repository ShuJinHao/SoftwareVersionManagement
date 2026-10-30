using Svm.Core.Instances;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Plc;
namespace Svm.InstanceService;

internal sealed class PlcUsageService(IPlcUsageRepository repository, IUnitOfWork unit, TimeProvider clock) : IPlcUsage
{
    public async Task<PlcUsageSummary> StateAsync(Guid id, long generation, bool protect, CancellationToken token)
    {
        var s = await repository.StateAsync(id, protect, token);
        return new(s?.Revision ?? 0, s?.BindingRevision == generation && s.CurrentRecordId is { } current ? View((await repository.GetAsync(current, token))!) : null);
    }
    public async Task<PlcUsageView> GetAsync(Guid id, CancellationToken token) => View(await repository.GetAsync(id, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound));
    public async Task<PlcUsageView> RecordAsync(RecordPlcUsageCommand x, Guid hardwareId, Guid softwareId, string version, string releaseState, Guid actor, string employee, CancellationToken token)
    {
        if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        var s = await repository.StateAsync(x.BindingId, true, token);
        if ((s?.Revision ?? 0) != x.ExpectedRevision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (x.FieldAt > clock.GetUtcNow()) throw new RequestRejectedException(RequestFailure.ValidationFailed);
        if (x.CorrectsId is { } correction)
        {
            var old = await repository.GetAsync(correction, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
            if (old.BindingId != x.BindingId || old.BindingRevision != x.BindingRevision || old.FieldAt != x.FieldAt || await repository.IsCorrectedAsync(correction, token))
                throw new RequestRejectedException(RequestFailure.InvalidState);
        }
        var previous = s?.BindingRevision == x.BindingRevision && s.CurrentRecordId is { } current ? await repository.GetAsync(current, token) : null;
        var record = new PlcUsageRecord(Guid.NewGuid(), hardwareId, x.BindingId, x.BindingRevision, softwareId, x.ReleaseId,
            version, releaseState, previous?.ReleaseId, x.FieldAt, clock.GetUtcNow(), actor, employee, x.Note, x.Materials, x.CorrectsId);
        var latest = await repository.LatestAsync(x.BindingId, x.BindingRevision, x.CorrectsId, token);
        if (s is null) { s = new(new(x.BindingId), x.BindingRevision); repository.Add(s); }
        s.Record(x.BindingRevision, latest is not null && latest.FieldAt > record.FieldAt ? latest.Id : record.Id);
        repository.Add(record); return View(record);
    }
    private static PlcUsageView View(PlcUsageRecord r) => new(r.Id,r.HardwareId,r.BindingId,r.BindingRevision,r.SoftwareId,r.ReleaseId,r.Version,r.ReleaseState,r.PreviousReleaseId,r.FieldAt,r.RecordedAt,r.ActorId,r.EmployeeNo,r.Note,r.Materials,r.CorrectsId);
}
