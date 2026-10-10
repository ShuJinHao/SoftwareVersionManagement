using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.Services.CrossCutting.Registration;
using Svm.Services.CrossCutting.Consumption;
using Svm.EntityFrameworkCore;
using Svm.Dapper;
using Svm.EventBus;
using Svm.ServiceDefaults;
using Svm.FileStorage;
using Svm.PackageService;
using Svm.ReleaseService;
using Svm.IdentityService;
using Svm.AuditService;
using Svm.Worker.Packages;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Tasks;
using Svm.TaskService;
using Svm.InstanceService;
using Svm.Worker.Tasks;

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
var tasks = TaskConfiguration.LoadFromEnvironment();
var files = PackageFileOptions.LoadFromEnvironment();
if (tasks is not null && files is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
var messaging = MessagingConfiguration.LoadFromEnvironment();
if (files is null)
{
    builder.Services.AddSvmApplication(); builder.Services.AddSvmConsumption([]);
}
else
{
    if (messaging is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    if (tasks is null) builder.Services.AddSvmPackageWorkerApplication(); else builder.Services.AddSvmTaskWorkerApplication();
    builder.Services.AddSvmConsumption(tasks is null ? ApplicationRegistration.PackageConsumers : ApplicationRegistration.TaskConsumers);
    builder.Services.AddSvmPackageFiles(files).AddSvmPackages().AddSvmReleases().AddSvmSoftwareCatalog().AddSvmPersonnelWorkAuthorization().AddSvmAudit();
    var site = SiteConfiguration.LoadFromEnvironment(); site.Require();
    if (site.SiteId != messaging.SiteId) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    builder.Services.AddSingleton(site); builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<PackageWorkerIdentity>();
    builder.Services.AddScoped<IPackageServiceIdentity>(p => p.GetRequiredService<PackageWorkerIdentity>());
    if (tasks is null) { builder.Services.AddScoped<ITrustedCallContextSource>(p => p.GetRequiredService<PackageWorkerIdentity>()); builder.Services.AddScoped<IIntegrationWorkAuthorizer, PackageWorkAuthorizer>(); }
    else
    {
        builder.Services.AddSingleton(tasks).AddSvmTasks().AddSvmManagedInstances();
        builder.Services.AddScoped<TaskWorkerIdentity>(); builder.Services.AddScoped<ITaskServiceIdentity>(p => p.GetRequiredService<TaskWorkerIdentity>());
        builder.Services.AddScoped<ITrustedCallContextSource>(p => p.GetRequiredService<TaskWorkerIdentity>());
        builder.Services.AddScoped<PackageWorkAuthorizer>(); builder.Services.AddScoped<IIntegrationWorkAuthorizer, TaskWorkAuthorizer>();
        builder.Services.AddHostedService<TaskExecutor>();
    }
    builder.Services.AddHostedService<PackageExecutor>();
}
var persistence = PersistenceConfiguration.LoadFromEnvironment();
builder.Services.AddSvmPostgres(persistence.WriterConnectionString);
builder.Services.AddSvmReadPersistence(persistence.ReaderConnectionString);
if (messaging is not null) builder.Services.AddSvmMessaging(messaging, delivery: true,
    consumers: files is null ? [] : tasks is null ? ApplicationRegistration.PackageConsumers : ApplicationRegistration.TaskConsumers);
builder.Services.ValidateSvmFoundation();
using var host = builder.Build();
await host.RunAsync();
