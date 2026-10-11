using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Packages;
using Svm.Application.Packages;
using Svm.Application.Releases;
using Svm.Services.Contracts.Releases;

namespace Svm.Application.Tasks;

internal sealed class TaskWorkerAuthorization(TaskAuthorization tasks, PackageAuthorization packages, GitLabAuthorization gitLab) : IRequestAuthorizer
{
    public ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest x, CancellationToken ct) => GitLabCapabilities.IsInternal(x.Request.GetType()) ? gitLab.AuthorizeAsync(x, null, ct) : TaskCapabilities.IsInternal(x.Request.GetType()) ? tasks.AuthorizeAsync(x, null, ct) :
        PackageCapabilities.IsWorker(x.Request.GetType()) ? packages.InternalAsync(x, ct) : ValueTask.FromResult(AuthorizationDecision.Deny(RequestFailure.PermissionDenied));
}
