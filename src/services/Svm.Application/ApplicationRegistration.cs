using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Registration;
using Svm.Services.Contracts.Identity;
using Svm.Application.Personnel;

namespace Svm.Application;

public static class ApplicationRegistration
{
    public static IServiceCollection AddSvmApplication(this IServiceCollection services) => services.AddSvmRequestPipeline([]);

    public static IServiceCollection AddSvmSessionApplication(this IServiceCollection services) => AddPersonnel(services, seed: false);
    public static IServiceCollection AddSvmSeedApplication(this IServiceCollection services) => AddPersonnel(services, seed: true);

    private static IServiceCollection AddPersonnel(IServiceCollection services, bool seed)
    {
        var bindings = RequestBinding.Discover(typeof(ApplicationRegistration).Assembly, typeof(IQuery<>).Assembly);
        services.AddSvmRequestPipeline(bindings.Where(b => (b.RequestType == typeof(SeedPersonnelCommand)) == seed).ToArray());
        services.AddScoped<IRequestAuthorizer, PersonnelAuthorization>();
        services.AddScoped<PersonnelCompletion>();
        return services;
    }
}
