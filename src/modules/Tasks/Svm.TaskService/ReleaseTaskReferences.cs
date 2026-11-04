using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Tasks;
using Svm.Services.Contracts.Packages;

namespace Svm.TaskService;

public static class ReleaseTaskReferenceRegistration
{
    // Read existing task facts even when creating new tasks is disabled in this host.
    public static IServiceCollection AddSvmReleaseTaskReferences(this IServiceCollection services) =>
        services.AddScoped<IReleaseTaskReferences, ReleaseTaskReferences>();
}
internal sealed class ReleaseTaskReferences(ITaskRepository repository) : IReleaseTaskReferences
{
    public Task<bool> HasUnfinishedAsync(Guid releaseId, CancellationToken token) => repository.HasUnfinishedReleaseAsync(releaseId, token);
}
