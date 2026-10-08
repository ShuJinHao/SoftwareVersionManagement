using Microsoft.Extensions.DependencyInjection;
using Svm.EntityFrameworkCore.Configuration;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Svm.Core.Identity;
using Svm.Core.Audit;
using Svm.EntityFrameworkCore.Identity;
using Svm.EntityFrameworkCore.Audit;
using Svm.EntityFrameworkCore.Operations;
using Svm.Core.Releases;
using Svm.Core.Packages;
using Svm.EntityFrameworkCore.Packages;
using Svm.Core.Instances;
using Svm.EntityFrameworkCore.Catalog;
using Svm.EntityFrameworkCore.Instances;

namespace Svm.EntityFrameworkCore;

public static class PersistenceRegistration
{
    public static IServiceCollection AddSvmPostgres(this IServiceCollection services, string connectionString)
    {
        if (services.Any(d => d.ServiceType == typeof(IUnitOfWork) || d.ServiceType == typeof(IOperationResultStore) || d.ServiceType == typeof(WriteDataSource)))
            throw new InvalidOperationException("PostgreSQL persistence is already registered.");
        services.AddSingleton(PostgresConnectionOptions.Parse(connectionString));
        services.AddSingleton<WriteDataSource>();
        services.AddScoped<SvmDbContext>();
        services.AddScoped<IUnitOfWork, PostgresUnitOfWork>();
        services.AddScoped<IOperationResultStore, OperationResultStore>();
        services.AddScoped<IPersonnelRepository, PersonnelRepository>();
        services.AddScoped<IPersonnelAdministrationRepository, PersonnelAdministrationRepository>();
        services.AddScoped<ISoftwareCatalogRepository, SoftwareCatalogRepository>();
        services.AddScoped<IReleaseRepository, ReleaseRepository>();
        services.AddScoped<IPackageRepository, PackageRepository>();
        services.AddScoped<ISiteAssetRepository, SiteAssetRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IInstanceAccessRepository, InstanceAccessRepository>();
        services.AddScoped<IManagedInstanceRepository, ManagedInstanceRepository>();
        return services;
    }
}
