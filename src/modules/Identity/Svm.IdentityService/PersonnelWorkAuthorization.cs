using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Identity;
using Svm.Services.Contracts.Packages;

namespace Svm.IdentityService;

public static class PersonnelWorkRegistration
{
    public static IServiceCollection AddSvmPersonnelWorkAuthorization(this IServiceCollection services) =>
        services.AddScoped<IPersonnelWorkAuthorization, PersonnelWorkAuthorization>();
}
internal sealed class PersonnelWorkAuthorization(IPersonnelRepository repository) : IPersonnelWorkAuthorization
{
    public async Task<bool> HasPermissionAsync(Guid subjectId, Guid softwareId, string operation, bool protect, CancellationToken token)
    {
        if (protect) await repository.LockSubjectAsync(subjectId, true, token);
        var user = await repository.GetAsync(subjectId, token);
        return user is { IsEnabled: true, MustChangePassword: false } &&
            (await repository.PermissionsAsync(subjectId, token)).Any(p => p.SoftwareId == softwareId && p.Operation == operation);
    }
}
