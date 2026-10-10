using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;
using Svm.Services.Contracts.Packages;
using Svm.Application.Packages;

namespace Svm.Application.Tasks;

internal sealed class TaskWorkerAuthorization(TaskAuthorization tasks, PackageAuthorization packages) : IRequestAuthorizer
{
    public ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest x, CancellationToken ct) => TaskCapabilities.IsInternal(x.Request.GetType()) ? tasks.AuthorizeAsync(x, null, ct) :
        PackageCapabilities.IsWorker(x.Request.GetType()) ? packages.InternalAsync(x, ct) : ValueTask.FromResult(AuthorizationDecision.Deny(RequestFailure.PermissionDenied));
}
