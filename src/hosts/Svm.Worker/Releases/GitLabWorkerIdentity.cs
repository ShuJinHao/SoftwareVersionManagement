using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;
using Svm.Worker.Packages;
using Svm.Worker.Tasks;

namespace Svm.Worker.Releases;

internal sealed class GitLabWorkerIdentity(PackageWorkerIdentity packages, TaskWorkerIdentity? tasks = null) : IGitLabServiceIdentity, ITrustedCallContextSource
{
    private static readonly AsyncLocal<(GitLabBinding Binding, Guid Work, ModuleOwner Owner)?> Current = new();
    public long SourceRevision => Current.Value?.Binding.SourceRevision ?? 1;
    public Guid? BindingId => Current.Value?.Binding.Id;
    public CallContextSnapshot? GetCurrent() => Current.Value is { } value ? new(new CallActor(ActorKind.Service, value.Binding.SubjectId,
        value.Binding.SoftwareId, workOwner: value.Owner, workId: value.Work), RequestKind.Internal, value.Work.ToString("D")) : tasks?.GetCurrent() ?? packages.GetCurrent();
    internal static IDisposable Enter(GitLabBinding binding, Guid work, ModuleOwner owner)
    { var previous = Current.Value; Current.Value = (binding, work, owner); return new Exit(() => Current.Value = previous); }
    private sealed class Exit(Action exit) : IDisposable { public void Dispose() => exit(); }
}
