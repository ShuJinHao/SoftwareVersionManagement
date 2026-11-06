namespace Svm.Core.Releases;

public sealed record CompatibilityDeclaration(Guid Id, Guid TargetReleaseId, long DeclarationRevision,
    Guid[] SourceReleaseIds, string DatabaseMode, string DatabaseRequirementsJson, string Statement, string Reason,
    Guid DeclaredBy, DateTimeOffset CreatedAt);
public interface ICompatibilityDeclarationRepository
{
    Task<CompatibilityDeclaration?> LatestAsync(Guid targetRelease, CancellationToken token);
    Task<CompatibilityDeclaration?> FindAsync(Guid id, CancellationToken token);
    void Add(CompatibilityDeclaration declaration);
}

/// <summary>Every reported database must match one explicit requirement; unknown or omitted facts never imply compatibility.</summary>
public static class DatabaseCompatibility
{
    public static bool Accepts(string expectedMode, IReadOnlyDictionary<string, IReadOnlyList<string>> requirements,
        string? reportedMode, IReadOnlyList<KeyValuePair<string, string>>? reported)
    {
        if (reportedMode != expectedMode || reported is null) return false;
        if (expectedMode == "None") return requirements.Count == 0 && reported.Count == 0;
        return expectedMode == "Present" && requirements.Count > 0 && reported.Count == requirements.Count &&
            reported.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() == reported.Count &&
            reported.All(x => requirements.TryGetValue(x.Key, out var schemas) && schemas.Contains(x.Value, StringComparer.Ordinal));
    }
}
