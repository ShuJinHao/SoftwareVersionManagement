using Svm.Application;
using Svm.Services.CrossCutting.Registration;
using Svm.EntityFrameworkCore;
using Svm.Dapper;
using Svm.EventBus;
using Svm.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Svm.EntityFrameworkCore.Identity;
using Svm.IdentityService;
using Svm.AuditService;
using Svm.Security;
using Svm.HttpApi.Personnel;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Catalog;
using Svm.HttpApi.Catalog;
using Svm.ReleaseService;
using Svm.InstanceService;
using Svm.HttpApi.Instances;
using Svm.Services.Contracts.Instances;
using Svm.FileStorage;
using Svm.PackageService;
using Svm.HttpApi.Packages;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Tasks;
using Svm.TaskService;
using Svm.HttpApi.Tasks;

namespace Svm.HttpApi;

public partial class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--operations-report" or "--recovery-packages")
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var probe = new ReadPersistenceProbe(PersistenceConfiguration.LoadFromEnvironment().ReaderConnectionString);
            try
            {
                if (args[0] == "--recovery-packages")
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(probe.RecoveryPackagesAsync(budget.Token).GetAwaiter().GetResult(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                    return;
                }
                var report = probe.OperationsAsync(budget.Token).GetAwaiter().GetResult();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                Environment.ExitCode = report.Status == "Available" ? 0 : 3;
            }
            catch { Console.Error.WriteLine("OPERATIONS_UNAVAILABLE; private details suppressed."); Environment.ExitCode = 3; }
            finally { probe.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return;
        }
        if (args.Length == 1 && args[0] is "--healthcheck" or "--diagnostics")
        { Environment.ExitCode = HostDiagnostics.ProbeAsync(HostRuntimeConfiguration.Load(), args[0] == "--diagnostics").GetAwaiter().GetResult(); return; }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        var runtime = HostRuntimeConfiguration.Load();
        SafeConsoleFormatter.Configure(builder.Logging);
        var taskOptions = TaskConfiguration.LoadFromEnvironment();
        var packageFiles = PackageFileOptions.LoadFromEnvironment();
        var gitLab = GitLabOptions.LoadFromEnvironment();
        if (gitLab is not null && packageFiles is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var messaging = MessagingConfiguration.LoadFromEnvironment();
        if (packageFiles is not null && messaging is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        if (taskOptions is not null && packageFiles is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        if (taskOptions is not null) builder.Services.AddSvmTaskApplication();
        else if (packageFiles is null) builder.Services.AddSvmInstanceApplication();
        else builder.Services.AddSvmPackageApplication();
        var persistence = PersistenceConfiguration.LoadFromEnvironment();
        var personnel = PersonnelConfiguration.LoadFromEnvironment();
        builder.Services.AddSvmPostgres(persistence.WriterConnectionString);
        builder.Services.AddSvmReadPersistence(persistence.ReaderConnectionString);
        builder.Services.AddSvmUserQueries();
        builder.Services.AddSvmCatalogQueries().AddSvmSoftwareCatalog().AddSvmSiteAssets();
        var site = SiteConfiguration.LoadFromEnvironment();
        if (packageFiles is not null && site.Require().SiteId != messaging!.SiteId) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        builder.Services.AddSingleton(site);
        builder.Services.AddScoped<CatalogCursor>();
        builder.Services.AddScoped<HardwareCursor>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<InstanceCursor>();
        builder.Services.AddSingleton(InstanceAccessConfiguration.LoadFromEnvironment());
        builder.Services.AddSingleton(PlcConfiguration.LoadFromEnvironment());
        builder.Services.AddSvmInstanceAccess().AddSvmManagedInstances().AddSvmReleases();
        builder.Services.AddSingleton(personnel.Management);
        builder.Services.AddScoped<UserCursor>();
        if (messaging is not null) builder.Services.AddSvmMessaging(messaging, delivery: false);
        builder.Services.AddScoped<PackageCursor>();
        builder.Services.AddScoped<TaskCursor>();
        if (taskOptions is not null) builder.Services.AddSingleton(taskOptions).AddSvmTasks().AddSvmTaskQueries();
        builder.Services.AddScoped<HttpPackageIdentity>();
        builder.Services.AddScoped<IPackageServiceIdentity>(p => p.GetRequiredService<HttpPackageIdentity>());
        builder.Services.AddScoped<IPackageDownloadProof>(p => p.GetRequiredService<HttpPackageIdentity>());
        if (packageFiles is not null)
            builder.Services.AddSvmPackageFiles(packageFiles).AddSvmPackages().AddSvmReleaseQueries().AddSvmPersonnelWorkAuthorization().AddSvmReleaseTaskReferences();
        if (gitLab is not null) builder.Services.AddSvmGitLab(gitLab);
        builder.Services.AddSvmPersonnel().AddSvmPersonnelAdministration().AddSvmPersonnelSoftwareAdministration().AddSvmAudit().AddSvmPersonnelCrypto(personnel.Policy);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<HttpPersonnelContext>();
        builder.Services.AddScoped<IAccessProofSource, HttpAccessProofSource>();
        builder.Services.AddScoped<ISessionProofSource>(p => p.GetRequiredService<HttpPersonnelContext>());
        builder.Services.AddScoped<ITrustedCallContextSource>(p => p.GetRequiredService<HttpPersonnelContext>());
        builder.Services.AddScoped<PersonnelCookieEvents>();
        using var certificate = ProtectionCertificate.Load(personnel.CertificatePath, personnel.CertificatePassword);
        builder.Services.AddDataProtection().SetApplicationName(personnel.ApplicationName)
            .PersistSvmKeysToDatabase().ProtectKeysWithCertificate(certificate);
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-Svm.Csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = SessionEndpoints.CookieName; options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always; options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.SlidingExpiration = false; options.ExpireTimeSpan = TimeSpan.FromHours(personnel.Policy.SessionHours);
            options.EventsType = typeof(PersonnelCookieEvents);
        });
        using var publicCertificate = runtime.PublicCertificatePath is null ? null
            : PackageFileOptions.LoadCertificate(runtime.PublicCertificatePath, runtime.PublicCertificatePassword);
        using var internalCertificate = packageFiles is null ? null : PackageFileOptions.LoadCertificate(packageFiles.ServerCertificatePath, packageFiles.ServerCertificatePassword);
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (publicCertificate is not null) options.ConfigureHttpsDefaults(https => https.ServerCertificate = publicCertificate);
            options.Limits.MaxRequestBodySize = 8192;
            if (packageFiles is not null)
            {
                HttpPackageIdentity.Listen(options, packageFiles, internalCertificate!);
                if (builder.Configuration["urls"] is { } urls)
                    foreach (var value in urls.Split(';'))
                    {
                        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                            !System.Net.IPAddress.TryParse(uri.Host, out var address) || uri.Port == packageFiles.InternalListenPort)
                            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
                        options.Listen(address, uri.Port, listener => listener.UseHttps());
                    }
            }
        });
        builder.Services.AddHealthChecks();
        var databaseProbe = new ReadPersistenceProbe(persistence.ReaderConnectionString);
        builder.Services.AddSingleton<IHostedService>(provider => new HostDiagnostics(runtime, "HttpApi", databaseProbe.CheckAsync,
            provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>(), async token =>
            {
                var db = await databaseProbe.OperationsAsync(token, packageFiles?.Execution.ReplicaCheckSeconds * 2);
                var metrics = db.Metrics.Concat(await PackageOperationsProbe.ReadAsync(packageFiles, token)).ToArray();
                return new OperationsSnapshot(db.ObservedAt, metrics.Any(m => m.Status == "Unavailable") ? "Incomplete" : "Available", metrics);
            }));
        builder.Services.ValidateSvmFoundation();

        if (args.Length == 1 && args[0] == "--validate-config")
        {
            using var validation = builder.Build();
            Console.WriteLine("CONFIGURATION_VALID HttpApi; no listeners, consumers, migration or seed started.");
            databaseProbe.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }
        var app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(() => databaseProbe.DisposeAsync().AsTask().GetAwaiter().GetResult());
        app.UseForwardedHeaders(runtime.Forwarding());
        app.Use(async (http, next) =>
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { await next(); }
            finally
            {
                var operation = Guid.TryParse(http.Request.Headers["Idempotency-Key"].ToString(), out var id) ? id : (Guid?)null;
                app.Logger.LogInformation("Request completed {Method} {StatusCode} {ElapsedMs} {TraceId} {OperationId}",
                    http.Request.Method, http.Response.StatusCode, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds, http.TraceIdentifier, operation);
            }
        });
        app.Use(SessionErrors.Handle);
        app.Use(RecoveryFence.Load().InvokeAsync);
        app.Use(HttpPackageIdentity.VerifyPeer);
        app.Use(HttpAccessProofSource.Authenticate);
        app.UseAuthentication();
        app.Use(async (http, next) =>
        {
            if (!HttpAccessProofSource.IsMachine(http) && http.Request.Path.StartsWithSegments("/api") && http.Request.Cookies.ContainsKey(SessionEndpoints.CookieName) &&
                http.User.Identity?.IsAuthenticated != true)
                throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapPersonnelSessions();
        app.MapPersonnelManagement();
        app.MapRoles();
        app.MapSiteCatalog();
        app.MapInstanceAccess();
        app.MapPackages(packageFiles is not null);
        Svm.HttpApi.Plc.PlcEndpoints.MapPlc(app, packageFiles is not null);
        app.MapGitLabReleases(packageFiles is not null);
        app.MapTasks(taskOptions is not null);
        app.MapPackageInternal(packageFiles is not null);
        app.Map("/api/{**path}", (HttpContext http) => Results.Json(new { code = "RESOURCE_NOT_FOUND", traceId = http.TraceIdentifier, retryable = false }, statusCode: 404));
        app.MapFallbackToFile("{*path:nonfile}", "index.html");
        app.Run();
    }
}
