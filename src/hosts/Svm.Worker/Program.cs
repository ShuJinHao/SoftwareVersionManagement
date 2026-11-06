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
using Svm.Worker.Releases;
using Svm.Services.Contracts.Releases;

if (args.Length == 1 && args[0] is "--healthcheck" or "--diagnostics")
{ Environment.ExitCode = await HostDiagnostics.ProbeAsync(HostRuntimeConfiguration.Load(), args[0] == "--diagnostics"); return; }
var builder = Host.CreateApplicationBuilder(args);
if (!(args.Length == 1 && args[0] == "--validate-config")) RecoveryFence.Load().RequireWorkerOpen();
SafeConsoleFormatter.Configure(builder.Logging);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
var runtime = HostRuntimeConfiguration.Load();
var tasks = TaskConfiguration.LoadFromEnvironment();
var files = PackageFileOptions.LoadFromEnvironment();
var gitLab = GitLabOptions.LoadFromEnvironment();
if (gitLab is not null && files is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
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
    builder.Services.AddSvmPackageFiles(files).AddSvmPackages().AddSvmReleases().AddSvmSoftwareCatalog().AddSvmPersonnelWorkAuthorization().AddSvmSiteAssets().AddSvmAudit();
    var site = SiteConfiguration.LoadFromEnvironment(); site.Require();
    if (site.SiteId != messaging.SiteId) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    builder.Services.AddSingleton(site); builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<PackageWorkerIdentity>();
    builder.Services.AddScoped<IPackageServiceIdentity>(p => p.GetRequiredService<PackageWorkerIdentity>());
    if (tasks is null) { if (gitLab is null) builder.Services.AddScoped<ITrustedCallContextSource>(p => p.GetRequiredService<PackageWorkerIdentity>()); builder.Services.AddScoped<IIntegrationWorkAuthorizer, PackageWorkAuthorizer>(); }
    else
    {
        builder.Services.AddSingleton(tasks).AddSvmTasks().AddSvmManagedInstances();
        builder.Services.AddScoped<TaskWorkerIdentity>(); builder.Services.AddScoped<ITaskServiceIdentity>(p => p.GetRequiredService<TaskWorkerIdentity>());
        if (gitLab is null) builder.Services.AddScoped<ITrustedCallContextSource>(p => p.GetRequiredService<TaskWorkerIdentity>());
        builder.Services.AddScoped<PackageWorkAuthorizer>(); builder.Services.AddScoped<IIntegrationWorkAuthorizer, TaskWorkAuthorizer>();
        builder.Services.AddHostedService<TaskExecutor>();
    }
    builder.Services.AddHostedService<PackageExecutor>();
    if (gitLab is not null)
    {
        builder.Services.AddSvmGitLab(gitLab);
        builder.Services.AddScoped<GitLabWorkerIdentity>();
        builder.Services.AddScoped<IGitLabServiceIdentity>(p => p.GetRequiredService<GitLabWorkerIdentity>());
        builder.Services.AddScoped<ITrustedCallContextSource>(p => p.GetRequiredService<GitLabWorkerIdentity>());
        builder.Services.AddHostedService<GitLabExecutor>();
    }
}
var persistence = PersistenceConfiguration.LoadFromEnvironment();
builder.Services.AddSvmPostgres(persistence.WriterConnectionString);
builder.Services.AddSvmReadPersistence(persistence.ReaderConnectionString);
if (messaging is not null) builder.Services.AddSvmMessaging(messaging, delivery: true,
    consumers: files is null ? [] : tasks is null ? ApplicationRegistration.PackageConsumers : ApplicationRegistration.TaskConsumers);
builder.Services.AddHealthChecks();
await using var databaseProbe = new ReadPersistenceProbe(persistence.ReaderConnectionString);
builder.Services.AddSingleton<IHostedService>(provider => new HostDiagnostics(runtime, "Worker", databaseProbe.CheckAsync,
    provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>(), async token =>
    {
        var db = await databaseProbe.OperationsAsync(token, files?.Execution.ReplicaCheckSeconds * 2);
        var metrics = db.Metrics.Concat(await PackageOperationsProbe.ReadAsync(files, token)).ToArray();
        return new OperationsSnapshot(db.ObservedAt, metrics.Any(m => m.Status == "Unavailable") ? "Incomplete" : "Available", metrics);
    }));
builder.Services.ValidateSvmFoundation();
using var host = builder.Build();
if (args.Length == 1 && args[0] == "--validate-config")
{ Console.WriteLine("CONFIGURATION_VALID Worker; no listeners, consumers, migration or seed started."); return; }
await host.RunAsync();
