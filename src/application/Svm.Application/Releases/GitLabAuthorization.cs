using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Releases;

namespace Svm.Application.Releases;

internal sealed class GitLabAuthorization(IReleaseImports imports, GitLabImportOptions options, ISoftwareCatalog software,
    IUnitOfWork unit, Svm.Application.Personnel.SiteAuthorization scope, IGitLabServiceIdentity? service = null)
{
    public async ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, PersonnelView? person, CancellationToken token)
    {
        var workId = request.Request switch {
            GetReleaseSyncWorkQuery q => q.WorkId, ClaimGitLabWorkCommand c => c.WorkId, GetGitLabWorkQuery q => q.WorkId,
            ApplyGitLabScanCommand c => c.Lease.WorkId, BeginGitLabImportCommand c => c.Lease.WorkId,
            FinishGitLabImportCommand c => c.Lease.WorkId, RenewGitLabWorkCommand c => c.Lease.WorkId, DelayGitLabWorkCommand c => c.Lease.WorkId, _ => (Guid?)null };
        var work = workId is { } id ? await imports.WorkAsync(id, false, token) : null;
        var softwareId = request.Request switch {
            ChangeReleaseSourceCommand c => c.SoftwareId, GetReleaseSourceOptionsQuery q => q.SoftwareId, GetReleaseSourceQuery q => q.SoftwareId, GetGitLabSourceQuery q => q.SoftwareId, RequestReleaseSyncCommand c => c.SoftwareId, ScheduleGitLabScanCommand c => c.SoftwareId,
            _ => work?.SoftwareId ?? Guid.Empty };
        if (GitLabCapabilities.IsInternal(request.Request.GetType()))
        {
            var binding = await imports.ResolveBindingAsync(softwareId, work?.BindingId, unit.CurrentOperationId is not null, token);
            if (binding is null || !options.IsAvailable(binding) || service?.BindingId != binding.Id || service.SourceRevision != binding.SourceRevision || request.Context.Actor.Kind != ActorKind.Service ||
                request.Context.Actor.ActorId != binding.SubjectId || request.Context.Actor.SoftwareId != softwareId ||
                request.Context.Actor.WorkId != (workId ?? binding.Id) || request.Context.Actor.WorkOwner != request.Policy.Owner || work is not null && work.BindingId != binding.Id)
                return AuthorizationDecision.Deny(RequestFailure.PermissionDenied);
        }
        else if (person is null || !await scope.SoftwareAsync(person.Access, request.Policy.Permission!, softwareId, token))
            return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if (!await software.ExistsAsync(softwareId, unit.CurrentOperationId is not null, token)) return AuthorizationDecision.Deny(RequestFailure.ResourceNotFound);
        if ((await software.GetAsync(softwareId, false, token)).Category == "PLC") return AuthorizationDecision.Deny(RequestFailure.InvalidState);
        return GitLabCapabilities.IsInternal(request.Request.GetType())
            ? AuthorizationDecision.Allow(AuthorizationTarget.Work(request.Policy.Owner, workId ?? service!.BindingId!.Value, softwareId))
            : AuthorizationDecision.Allow(AuthorizationTarget.Software(softwareId));
    }
}
