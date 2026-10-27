using Svm.Services.Contracts.Framework;

namespace Svm.Services.Contracts.Releases;

public sealed record GitLabConnection(Guid Id, string Name, string BaseUri, IReadOnlyList<GitLabProject> Projects, bool Enabled = true);
public sealed record GitLabProject(long Id, string Name, IReadOnlyList<string> Packages);
public sealed record GitLabSourceOption(Guid ConnectionId, string ConnectionName, string BaseUri, long ProjectId, string ProjectName, string PackageName);
public sealed record ReleaseSourceOptionsView(IReadOnlyList<GitLabSourceOption> Items, int? NextPage, ReleaseSourceView Current);

[RequestPolicy("rel.ChangeReleaseSourceCommand", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.DatabaseAtomic, IdempotencyMode.OperationResult, ValidationMode.Required, ActorKind.Human, Permission = "release.source.manage")]
public sealed record ChangeReleaseSourceCommand(Guid Key, Guid SoftwareId, Guid ConnectionId, long ProjectId, string PackageName,
    bool Enabled, long ExpectedRevision, string Reason) : ICommand<ReleaseSourceView>;

[RequestPolicy("rel.GetReleaseSourceOptionsQuery", ModuleOwner.Releases, RequestKind.Manage, RequestScope.Software,
    TransactionMode.ReadOnly, IdempotencyMode.None, ValidationMode.Required, ActorKind.Human, Permission = "release.source.manage")]
public sealed record GetReleaseSourceOptionsQuery(Guid SoftwareId, string? Search = null, int Page = 1, int PageSize = 50) : IQuery<ReleaseSourceOptionsView>;
