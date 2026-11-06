namespace Svm.Core.Packages;

/// <summary>Immutable initial intent; retries and changes of operator remain in the audit history.</summary>
public sealed record PackageCleanup(Guid Id, Guid PackageId, Guid RequestedBy, DateTimeOffset CreatedAt, string Reason);

/// <summary>Explicit operator evidence, never inferred from a timer or a missing gateway log.</summary>
public sealed record DownloadStopEvidence(Guid Id, Guid PackageId, string NodeId, Guid WorkerGeneration,
    DateTimeOffset StoppedAt, Guid RecordedBy, DateTimeOffset RecordedAt, string Evidence, string Reason);
public sealed record OpenDownloadGroup(string NodeId, Guid WorkerGeneration, long OpenCount, DateTimeOffset LatestStartedAt);
