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
    public static IServiceCollection AddSvmPersonnelManagementApplication(this IServiceCollection services) => AddPersonnel(services, seed: false, management: true);

    private static IServiceCollection AddPersonnel(IServiceCollection services, bool seed, bool management = false)
    {
        var bindings = RequestBinding.Discover(typeof(ApplicationRegistration).Assembly, typeof(IQuery<>).Assembly);
        services.AddSvmRequestPipeline(bindings.Where(b => seed ? b.RequestType == typeof(SeedPersonnelCommand) :
            b.RequestType != typeof(SeedPersonnelCommand) && (PersonnelWriteCapabilities.Contains(b.RequestType) ||
                b.RequestType == typeof(AnonymousSessionQuery) || b.RequestType == typeof(CurrentSessionQuery) ||
                management && (PersonnelManagementCapabilities.Contains(b.RequestType) || b.RequestType == typeof(GetUserQuery) || b.RequestType == typeof(ListUsersQuery)))).ToArray());
        services.AddScoped<IRequestAuthorizer, PersonnelAuthorization>();
        services.AddScoped<PersonnelCompletion>();
        if (management)
        {
            services.AddScoped<PersonnelAdministrationCompletion>();
            services.AddScoped<IIdempotencyRequestAdapter<CreateUserCommand, OperationResult<UserView>>, CreateUserAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<UpdateUserCommand, OperationResult<UserView>>, UpdateUserAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<ResetUserPasswordCommand, OperationResult<UserView>>, ResetUserPasswordAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<ReplaceUserPermissionsCommand, OperationResult<UserView>>, ReplaceUserPermissionsAdapter>();
        }
        return services;
    }
}
