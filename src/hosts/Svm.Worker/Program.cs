using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.Services.CrossCutting.Registration;
using Svm.EntityFrameworkCore;
using Svm.Dapper;
using Svm.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
}));
builder.Services.AddSvmApplication();
var persistence = PersistenceConfiguration.LoadFromEnvironment();
builder.Services.AddSvmPostgres(persistence.WriterConnectionString);
builder.Services.AddSvmReadPersistence(persistence.ReaderConnectionString);
builder.Services.ValidateSvmFoundation();
using var host = builder.Build();
await host.RunAsync();
