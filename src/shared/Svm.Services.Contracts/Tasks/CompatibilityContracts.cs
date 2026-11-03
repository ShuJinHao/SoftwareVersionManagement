using System.Text.Json.Serialization;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;

namespace Svm.Services.Contracts.Tasks;

public sealed record DatabaseRequirement([property: JsonRequired] string DatabaseKey,
    [property: JsonRequired] IReadOnlyList<string> AcceptedSchemaIds);
public sealed record CompatibilityInput([property: JsonRequired] IReadOnlyList<Guid> SourceReleaseIds,
    [property: JsonRequired] string DatabaseMode, [property: JsonRequired] IReadOnlyList<DatabaseRequirement> DatabaseRequirements,
    [property: JsonRequired] string Statement, [property: JsonRequired] string Reason);
public sealed record CompatibilityView(Guid Id, Guid TargetReleaseId, long DeclarationRevision,
    IReadOnlyList<Guid> SourceReleaseIds, string DatabaseMode, IReadOnlyList<DatabaseRequirement> DatabaseRequirements,
    string Statement, string Reason, Guid DeclaredBy, DateTimeOffset CreatedAt);
/// <summary>The immutable facts used at admission or when granting permission to start; never an installation result.</summary>
public sealed record CompatibilityDecision(Guid DeclarationId, long DeclarationRevision, Guid SourceReleaseId,
    DatabaseState DatabaseState);
public sealed record CompatibilityCheck(string? RejectionCode, CompatibilityDecision? Decision);
public interface ICompatibilityDeclarations
{
    Task<CompatibilityView> RecordAsync(Guid id, Guid targetRelease, CompatibilityInput input, Guid actor, CancellationToken token);
    Task<CompatibilityView> FindAsync(Guid id, CancellationToken token);
    Task<CompatibilityCheck> CheckAsync(Guid targetRelease, Guid? sourceRelease, DatabaseState? databases,
        long? expectedRevision, CancellationToken token);
}

[RequestPolicy("releases.RecordCompatibilityCommand", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record RecordCompatibilityCommand(Guid Key, Guid ReleaseId, CompatibilityInput Input) : ICommand<CompatibilityView>;
[RequestPolicy("releases.GetCompatibilityDeclarationsQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetCompatibilityDeclarationsQuery(Guid ReleaseId, int PageSize = 50, Guid? After = null) : IQuery<IReadOnlyList<CompatibilityView>>;
