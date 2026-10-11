using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Services.Contracts.Releases;

[RequestPolicy("rel.RequestReleaseSyncCommand", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.upload")]
public sealed record RequestReleaseSyncCommand(Guid Key, Guid SoftwareId, string? Tag = null) : ICommand<ReleaseSyncWorkView>;

[RequestPolicy("rel.GetReleaseSourceQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetReleaseSourceQuery(Guid SoftwareId) : IQuery<ReleaseSourceView>;

[RequestPolicy("rel.GetReleaseSyncWorkQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "software.read")]
public sealed record GetReleaseSyncWorkQuery(Guid WorkId) : IQuery<ReleaseSyncWorkView>;

[RequestPolicy("rel.ScheduleGitLabScanCommand", ModuleOwner.Releases, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record ScheduleGitLabScanCommand(Guid SoftwareId) : ICommand<ReleaseSyncWorkView>;

[RequestPolicy("rel.ClaimGitLabWorkCommand", ModuleOwner.Releases, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record ClaimGitLabWorkCommand(Guid WorkId, Guid LeaseToken) : ICommand<GitLabWork?>;

[RequestPolicy("rel.ApplyGitLabScanCommand", ModuleOwner.Releases, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record ApplyGitLabScanCommand(GitLabLease Lease, IReadOnlyList<GitLabReleaseFacts> Releases, IReadOnlyList<string> Tags, int? NextPage) : ICommand<bool>;

[RequestPolicy("rel.BeginGitLabImportCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record BeginGitLabImportCommand(GitLabLease Lease, GitLabReleaseFacts Facts, Guid ReceiveToken, string NodeId) : ICommand<GitLabImportReceipt>;

[RequestPolicy("rel.FinishGitLabImportCommand", ModuleOwner.Packages, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record FinishGitLabImportCommand(GitLabLease Lease, UploadReceipt Receipt, long Size, string Sha256) : ICommand<bool>;

[RequestPolicy("rel.RenewGitLabWorkCommand", ModuleOwner.Releases, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record RenewGitLabWorkCommand(GitLabLease Lease) : ICommand<bool>;

[RequestPolicy("rel.DelayGitLabWorkCommand", ModuleOwner.Releases, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.DatabaseAtomic, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record DelayGitLabWorkCommand(GitLabLease Lease, string Code) : ICommand<bool>;

[RequestPolicy("rel.GetGitLabWorkQuery", ModuleOwner.Releases, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record GetGitLabWorkQuery(Guid WorkId) : IQuery<GitLabWork>;

[RequestPolicy("rel.GetGitLabSourceQuery", ModuleOwner.Releases, RequestKind.Internal, RequestScope.InternalWork,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Service, Permission = "release.import")]
public sealed record GetGitLabSourceQuery(Guid SoftwareId) : IQuery<ReleaseSourceView>;

public static class GitLabCapabilities
{
    public static bool IsWrite(Type type) => type == typeof(RequestReleaseSyncCommand) || type == typeof(ScheduleGitLabScanCommand) || type == typeof(ClaimGitLabWorkCommand) || type == typeof(ApplyGitLabScanCommand) || type == typeof(BeginGitLabImportCommand) || type == typeof(FinishGitLabImportCommand) || type == typeof(RenewGitLabWorkCommand) || type == typeof(DelayGitLabWorkCommand);
    public static bool IsQuery(Type type) => type == typeof(GetReleaseSourceQuery) || type == typeof(GetReleaseSyncWorkQuery) || type == typeof(GetGitLabWorkQuery) || type == typeof(GetGitLabSourceQuery);
    public static bool IsInternal(Type type) => IsWrite(type) && type != typeof(RequestReleaseSyncCommand) || type == typeof(GetGitLabWorkQuery) || type == typeof(GetGitLabSourceQuery);
    public static bool Contains(Type type) => IsWrite(type) || IsQuery(type);
    public static ModuleOwner Owner(Type type) => type == typeof(FinishGitLabImportCommand) || type == typeof(BeginGitLabImportCommand) ? ModuleOwner.Packages : ModuleOwner.Releases;
}
