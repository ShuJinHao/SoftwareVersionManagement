using Svm.Core.Releases;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;

namespace Svm.ReleaseService;

internal sealed class IntegrationMaterials(IIntegrationMaterialRepository repository, IUnitOfWork unit, TimeProvider clock) : IIntegrationMaterials
{
    public async Task<IntegrationMaterialView> RecordAsync(Guid id, Guid release, IntegrationMaterialInput x, Guid actor, CancellationToken ct)
    { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
      var m = new IntegrationMaterial(id, release, checked(await repository.LatestRevisionAsync(release, ct) + 1),
          x.DataLocations.ToArray(), x.UpdateBehavior, x.RollbackBehavior, x.RecoveryPlan, x.VerificationConclusion, x.EvidenceReferences.ToArray(), x.Reason, actor, clock.GetUtcNow()); repository.Add(m); return View(m); }
    public async Task<IReadOnlyList<IntegrationMaterialView>> GetAsync(Guid release, int take, Guid? after, CancellationToken ct) => (await repository.GetAsync(release, take, after, ct)).Select(View).ToArray();
    public async Task<IntegrationMaterialView> FindAsync(Guid id, CancellationToken ct) => View(await repository.FindAsync(id, ct) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound));
    private static IntegrationMaterialView View(IntegrationMaterial m) => new(m.Id, m.ReleaseId, m.Revision, m.DataLocations, m.UpdateBehavior, m.RollbackBehavior, m.RecoveryPlan, m.VerificationConclusion, m.EvidenceReferences, m.Reason, m.RecordedBy, m.RecordedAt);
}
