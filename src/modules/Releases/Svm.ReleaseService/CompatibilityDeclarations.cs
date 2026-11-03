using System.Text.Json;
using Svm.Core.Releases;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Instances;

namespace Svm.ReleaseService;

internal sealed class CompatibilityDeclarations(ICompatibilityDeclarationRepository repository, IReleaseRepository releases,
    IUnitOfWork unit, TimeProvider clock) : ICompatibilityDeclarations
{
    public async Task<CompatibilityView> RecordAsync(Guid id, Guid targetRelease, CompatibilityInput x, Guid actor, CancellationToken ct)
    {
        if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        // The target release lock serializes revisions and is held until the caller's root transaction commits.
        var target = await releases.GetAsync(targetRelease, true, ct) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (target.State != "Formal" || target.SourceKind == "PLC") throw new RequestRejectedException(RequestFailure.InvalidState);
        foreach (var sourceId in x.SourceReleaseIds)
        {
            var source = await releases.GetAsync(sourceId, false, ct);
            if (source?.SoftwareId != target.SoftwareId) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
            if (sourceId == targetRelease) throw new RequestRejectedException(RequestFailure.ValidationFailed);
        }
        var revision = checked((await repository.LatestAsync(targetRelease, ct))?.DeclarationRevision + 1 ?? 1);
        var declaration = new CompatibilityDeclaration(id, targetRelease, revision, x.SourceReleaseIds.ToArray(), x.DatabaseMode,
            JsonSerializer.Serialize(x.DatabaseRequirements), x.Statement, x.Reason, actor, clock.GetUtcNow());
        repository.Add(declaration); return View(declaration);
    }
    public async Task<CompatibilityView> FindAsync(Guid id, CancellationToken ct) =>
        View(await repository.FindAsync(id, ct) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound));
    public async Task<CompatibilityCheck> CheckAsync(Guid target, Guid? source, DatabaseState? databases, long? expectedRevision, CancellationToken ct)
    {
        var declaration = await repository.LatestAsync(target, ct);
        if (declaration is null) return new("COMPATIBILITY_REQUIRED", null);
        if (expectedRevision is { } revision && revision != declaration.DeclarationRevision) return new("COMPATIBILITY_REVISION_CONFLICT", null);
        if (source is null || !declaration.SourceReleaseIds.Contains(source.Value)) return new("SOURCE_RELEASE_INCOMPATIBLE", null);
        var requirements = JsonSerializer.Deserialize<DatabaseRequirement[]>(declaration.DatabaseRequirementsJson)!
            .ToDictionary(x => x.DatabaseKey, x => x.AcceptedSchemaIds, StringComparer.Ordinal);
        if (!DatabaseCompatibility.Accepts(declaration.DatabaseMode, requirements, databases?.Mode,
            databases?.Items.Select(x => new KeyValuePair<string, string>(x.DatabaseKey, x.SchemaId)).ToArray()))
            return new("DATABASE_INCOMPATIBLE", null);
        return new(null, new(declaration.Id, declaration.DeclarationRevision, source.Value,
            new(databases!.Mode, databases.Items.ToArray())));
    }
    private static CompatibilityView View(CompatibilityDeclaration d) => new(d.Id, d.TargetReleaseId, d.DeclarationRevision,
        d.SourceReleaseIds, d.DatabaseMode, JsonSerializer.Deserialize<DatabaseRequirement[]>(d.DatabaseRequirementsJson)!,
        d.Statement, d.Reason, d.DeclaredBy, d.CreatedAt);
}
