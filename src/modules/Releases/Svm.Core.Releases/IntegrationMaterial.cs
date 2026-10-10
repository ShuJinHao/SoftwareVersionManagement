namespace Svm.Core.Releases;

public sealed record IntegrationMaterial(Guid Id, Guid ReleaseId, long Revision, string[] DataLocations,
    string UpdateBehavior, string RollbackBehavior, string RecoveryPlan, string? VerificationConclusion,
    string[] EvidenceReferences, string Reason, Guid RecordedBy, DateTimeOffset RecordedAt);
public interface IIntegrationMaterialRepository
{
    Task<IReadOnlyList<IntegrationMaterial>> GetAsync(Guid release, int take, Guid? after, CancellationToken token);
    Task<IntegrationMaterial?> FindAsync(Guid id, CancellationToken token);
    Task<long> LatestRevisionAsync(Guid release, CancellationToken token);
    void Add(IntegrationMaterial material);
}
