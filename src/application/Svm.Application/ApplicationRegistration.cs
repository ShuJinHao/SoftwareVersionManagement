using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.CrossCutting.Registration;
using Svm.Services.Contracts.Identity;
using Svm.Application.Personnel;
using Svm.Application.Catalog;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Instances;
using Svm.Application.Instances;

namespace Svm.Application;

public static class ApplicationRegistration
{
    public static IServiceCollection AddSvmApplication(this IServiceCollection services) => services.AddSvmRequestPipeline([]);

    public static IServiceCollection AddSvmSessionApplication(this IServiceCollection services) => AddPersonnel(services, seed: false);
    public static IServiceCollection AddSvmSeedApplication(this IServiceCollection services) => AddPersonnel(services, seed: true);
    public static IServiceCollection AddSvmPersonnelManagementApplication(this IServiceCollection services) => AddPersonnel(services, seed: false, management: true);
    public static IServiceCollection AddSvmSiteCatalogApplication(this IServiceCollection services) => AddPersonnel(services, seed: false, management: true, siteCatalog: true);

    public static IServiceCollection AddSvmInstanceApplication(this IServiceCollection services) => AddPersonnel(services, seed: false, management: true, siteCatalog: true, instanceAccess: true);

    private static IServiceCollection AddPersonnel(IServiceCollection services, bool seed, bool management = false, bool siteCatalog = false, bool instanceAccess = false)
    {
        var bindings = RequestBinding.Discover(typeof(ApplicationRegistration).Assembly, typeof(IQuery<>).Assembly);
        services.AddSvmRequestPipeline(bindings.Where(b => seed ? b.RequestType == typeof(SeedPersonnelCommand) :
            b.RequestType != typeof(SeedPersonnelCommand) && (PersonnelWriteCapabilities.Contains(b.RequestType) ||
                b.RequestType == typeof(AnonymousSessionQuery) || b.RequestType == typeof(CurrentSessionQuery) ||
                management && (PersonnelManagementCapabilities.Contains(b.RequestType) || b.RequestType == typeof(GetUserQuery) || b.RequestType == typeof(ListUsersQuery)) ||
                siteCatalog && (CatalogCapabilities.IsWrite(b.RequestType) || CatalogCapabilities.IsQuery(b.RequestType)) ||
                instanceAccess && (InstanceCapabilities.IsWrite(b.RequestType) || InstanceCapabilities.IsQuery(b.RequestType)))).ToArray());
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
        if (siteCatalog)
        {
            services.AddScoped<CatalogCompletion>();
            services.AddScoped<IIdempotencyRequestAdapter<CreateSoftwareCommand, OperationResult<SoftwareView>>, CreateSoftwareCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<UpdateSoftwareCommand, OperationResult<SoftwareView>>, UpdateSoftwareCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<CreateProcessCommand, OperationResult<ProcessView>>, CreateProcessCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<UpdateProcessCommand, OperationResult<ProcessView>>, UpdateProcessCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<CreateDeviceCommand, OperationResult<DeviceView>>, CreateDeviceCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<UpdateDeviceCommand, OperationResult<DeviceView>>, UpdateDeviceCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<CreateBindingCommand, OperationResult<BindingView>>, CreateBindingCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<RevokeBindingCommand, OperationResult<BindingView>>, RevokeBindingCommandAdapter>();
        }
        if (instanceAccess)
        {
            services.AddScoped<InstanceAuthorization>(); services.AddScoped<InstanceCompletion>();
            services.AddScoped<IIdempotencyRequestAdapter<CreateEnrollmentGrantCommand,OperationResult<GrantView>>, CreateEnrollmentGrantCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<RevokeEnrollmentGrantCommand,OperationResult<GrantView>>, RevokeEnrollmentGrantCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<CreateRecoveryGrantCommand,OperationResult<GrantView>>, CreateRecoveryGrantCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<RevokeRecoveryGrantCommand,OperationResult<GrantView>>, RevokeRecoveryGrantCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<RevokeInstanceCredentialCommand,OperationResult<CredentialView>>, RevokeInstanceCredentialCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<UpdateInstanceLifecycleCommand,OperationResult<InstanceIdentity>>, UpdateInstanceLifecycleCommandAdapter>();
            services.AddScoped<IIdempotencyRequestAdapter<OpenReportStreamCommand,OperationResult<StreamResult>>, OpenReportStreamCommandAdapter>();
            services.AddScoped<IProtocolRequestAdapter<RegisterInstanceCommand,OperationResult<RegistrationResult>>, RegisterInstanceCommandAdapter>();
            services.AddScoped<IProtocolRequestAdapter<RecoverInstanceCommand,OperationResult<RegistrationResult>>, RecoverInstanceCommandAdapter>();
            services.AddScoped<IProtocolRequestAdapter<SubmitStatusReportCommand,OperationResult<ReportResult>>, SubmitStatusReportCommandAdapter>();
        }
        return services;
    }
}
