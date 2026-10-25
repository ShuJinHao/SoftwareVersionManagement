using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Identity;
using Svm.Services.Contracts.Packages;

namespace Svm.IdentityService;

public static class PersonnelWorkRegistration
{
    public static IServiceCollection AddSvmPersonnelWorkAuthorization(this IServiceCollection services) =>
        services.AddScoped<IPersonnelWorkAuthorization, PersonnelWorkAuthorization>();
}
internal sealed class PersonnelWorkAuthorization(IPersonnelRepository repository, IRoleRepository roles) : IPersonnelWorkAuthorization
{
    public async Task<bool> HasPermissionAsync(Guid subjectId, Guid softwareId, string operation, bool protect, CancellationToken token)
    {
        if (protect) await repository.LockSubjectAsync(subjectId, true, token);
        return await roles.HasSoftwarePermissionAsync(subjectId, softwareId, operation, token);
    }
    public async Task<bool> HasInstanceAsync(Guid subjectId, Guid softwareId, string operation, Guid instanceId, bool protect, CancellationToken token)
    {
        if (protect) await repository.LockSubjectAsync(subjectId, true, token);
        return await roles.HasInstancePermissionAsync(subjectId, softwareId, operation, instanceId, token);
    }
    public async Task<bool> CoversSelectionAsync(Guid subjectId, Guid softwareId, string operation, Guid selectionId, bool protect, CancellationToken token)
    {
        if (protect) await repository.LockSubjectAsync(subjectId, true, token);
        return await roles.CoversSelectionAsync(subjectId, softwareId, operation, selectionId, token);
    }
}
