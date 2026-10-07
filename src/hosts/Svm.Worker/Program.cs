using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.Services.CrossCutting.Registration;
using Svm.Services.CrossCutting.Consumption;
using Svm.EntityFrameworkCore;
using Svm.Dapper;
using Svm.EventBus;
using Svm.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
}));
builder.Services.AddSvmApplication();
builder.Services.AddSvmConsumption([]); // No approved business consumers; no Inbox cleaner is started.
var persistence = PersistenceConfiguration.LoadFromEnvironment();
builder.Services.AddSvmPostgres(persistence.WriterConnectionString);
builder.Services.AddSvmReadPersistence(persistence.ReaderConnectionString);
if (MessagingConfiguration.LoadFromEnvironment() is { } messaging)
    builder.Services.AddSvmMessaging(messaging, delivery: true);
builder.Services.ValidateSvmFoundation();
using var host = builder.Build();
await host.RunAsync();
