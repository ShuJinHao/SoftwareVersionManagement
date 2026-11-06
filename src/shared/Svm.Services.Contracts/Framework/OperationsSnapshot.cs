namespace Svm.Services.Contracts.Framework;

/// <summary>Private operational facts; never grants business authority or changes work state.</summary>
public sealed record OperationsSnapshot(DateTimeOffset ObservedAt, string Status, IReadOnlyList<OperationsMetric> Metrics);
public sealed record OperationsMetric(string Name, string Status, long? Count, bool Capped,
    DateTimeOffset? OldestAt, string TimeMeaning, IReadOnlyList<OperationsWork> Examples);
public sealed record OperationsWork(Guid Id, string State, string Stage, long Generation,
    DateTimeOffset? LeaseUntil, string? ErrorCode);
public sealed record RecoveryPackages(DateTimeOffset ObservedAt, IReadOnlyList<RecoveryPackage> Packages);
public sealed record RecoveryPackage(Guid Id, long Size, string Sha256);
