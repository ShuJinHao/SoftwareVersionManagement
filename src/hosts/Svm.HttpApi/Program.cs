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

namespace Svm.HttpApi;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        var packageFiles = PackageFileOptions.LoadFromEnvironment();
        var messaging = MessagingConfiguration.LoadFromEnvironment();
        if (packageFiles is not null && messaging is null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        if (packageFiles is null) builder.Services.AddSvmInstanceApplication();
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
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<InstanceCursor>();
        builder.Services.AddSingleton(InstanceAccessConfiguration.LoadFromEnvironment());
        builder.Services.AddSvmInstanceAccess().AddSvmManagedInstances();
        builder.Services.AddSingleton(personnel.Management);
        builder.Services.AddScoped<UserCursor>();
        if (messaging is not null) builder.Services.AddSvmMessaging(messaging, delivery: false);
        builder.Services.AddScoped<PackageCursor>();
        builder.Services.AddScoped<HttpPackageIdentity>();
        builder.Services.AddScoped<IPackageServiceIdentity>(p => p.GetRequiredService<HttpPackageIdentity>());
        builder.Services.AddScoped<IPackageDownloadProof>(p => p.GetRequiredService<HttpPackageIdentity>());
        if (packageFiles is not null)
            builder.Services.AddSvmPackageFiles(packageFiles).AddSvmPackages().AddSvmReleases().AddSvmReleaseQueries().AddSvmPersonnelWorkAuthorization();
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
        using var internalCertificate = packageFiles is null ? null : PackageFileOptions.LoadCertificate(packageFiles.ServerCertificatePath, packageFiles.ServerCertificatePassword);
        builder.WebHost.ConfigureKestrel(options =>
        {
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
        builder.Services.ValidateSvmFoundation();

        var app = builder.Build();
        app.Use(SessionErrors.Handle);
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
        app.MapSiteCatalog();
        app.MapInstanceAccess();
        app.MapPackages(packageFiles is not null);
        app.MapPackageInternal(packageFiles is not null);
        app.Map("/api/{**path}", (HttpContext http) => Results.Json(new { code = "RESOURCE_NOT_FOUND", traceId = http.TraceIdentifier, retryable = false }, statusCode: 404));
        app.MapFallbackToFile("{*path:nonfile}", "index.html");
        app.Run();
    }
}
