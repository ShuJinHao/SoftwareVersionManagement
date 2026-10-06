using Svm.Application;
using Svm.Services.CrossCutting.Registration;
using Svm.EntityFrameworkCore;
using Svm.Dapper;
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

namespace Svm.HttpApi;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.Services.AddSvmSessionApplication();
        var persistence = PersistenceConfiguration.LoadFromEnvironment();
        var personnel = PersonnelConfiguration.LoadFromEnvironment();
        builder.Services.AddSvmPostgres(persistence.WriterConnectionString);
        builder.Services.AddSvmReadPersistence(persistence.ReaderConnectionString);
        builder.Services.AddSvmPersonnel().AddSvmAudit().AddSvmPersonnelCrypto(personnel.Policy);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<HttpPersonnelContext>();
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
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8192);
        builder.Services.ValidateSvmFoundation();

        var app = builder.Build();
        app.Use(SessionErrors.Handle);
        app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("Authorization")) throw new RequestRejectedException(RequestFailure.CredentialInvalid);
            await next();
        });
        app.UseAuthentication();
        app.Use(async (http, next) =>
        {
            if (http.Request.Cookies.ContainsKey(SessionEndpoints.CookieName) && http.User.Identity?.IsAuthenticated != true)
                throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
            await next();
        });
        app.MapPersonnelSessions();
        app.Run();
    }
}
