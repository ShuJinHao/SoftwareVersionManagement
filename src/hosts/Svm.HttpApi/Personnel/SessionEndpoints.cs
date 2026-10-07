using System.Security.Claims;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.HttpApi.Personnel;

internal static class SessionEndpoints
{
    internal const string CookieName = "__Host-Svm.Session";
    internal static void MapPersonnelSessions(this WebApplication app)
    {
        app.MapGet("/api/v1/session", async (HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            var view = http.User.Identity?.IsAuthenticated == true
                ? await sender.Send(new CurrentSessionQuery(), http.RequestAborted)
                : await sender.Send(new AnonymousSessionQuery(), http.RequestAborted);
            return View(http, antiforgery, view);
        }).WithMetadata(new PersonnelEndpointKind(RequestKind.Session));
        app.MapPost("/api/v1/session", async (HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            await CheckCsrf(http, antiforgery);
            var input = await ReadInput(http, ["employeeNo", "password"]);
            var result = await sender.Send(new LoginCommand(input["employeeNo"], input["password"]), http.RequestAborted);
            return await Complete(http, antiforgery, result.Value);
        }).WithMetadata(new PersonnelEndpointKind(RequestKind.Session));
        app.MapDelete("/api/v1/session", async (HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            await CheckCsrf(http, antiforgery);
            await sender.Send(new LogoutCommand(), http.RequestAborted);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).WithMetadata(new PersonnelEndpointKind(RequestKind.Session));
        app.MapPost("/api/v1/session/password", async (HttpContext http, ISender sender, IAntiforgery antiforgery) =>
        {
            await CheckCsrf(http, antiforgery);
            var input = await ReadInput(http, ["currentPassword", "newPassword"]);
            var result = await sender.Send(new ChangePasswordCommand(input["currentPassword"], input["newPassword"]), http.RequestAborted);
            return await Complete(http, antiforgery, result.Value);
        }).WithMetadata(new PersonnelEndpointKind(RequestKind.Session));
    }
    private static async Task CheckCsrf(HttpContext http, IAntiforgery antiforgery)
    {
        if (!http.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        try { await antiforgery.ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { throw new RequestRejectedException(RequestFailure.PermissionDenied); }
    }
    private static async Task<Dictionary<string, string>> ReadInput(HttpContext http, string[] fields)
    {
        if (!http.Request.HasJsonContentType() || http.Request.ContentLength > 8192) throw new RequestRejectedException(RequestFailure.InvalidRequest);
        try
        {
            using var input = await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { MaxDepth = 4 }, http.RequestAborted);
            if (input.RootElement.ValueKind != JsonValueKind.Object) throw new RequestRejectedException(RequestFailure.InvalidRequest);
            var properties = input.RootElement.EnumerateObject().ToArray();
            if (properties.Any(p => !fields.Contains(p.Name))) throw new RequestRejectedException(RequestFailure.UnknownField);
            if (properties.Length != fields.Length || properties.Select(p => p.Name).Distinct().Count() != fields.Length ||
                properties.Any(p => p.Value.ValueKind != JsonValueKind.String)) throw new RequestRejectedException(RequestFailure.InvalidRequest);
            return properties.ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
        catch (JsonException) { throw new RequestRejectedException(RequestFailure.InvalidRequest); }
    }
    private static IResult View(HttpContext http, IAntiforgery antiforgery, PersonnelView? person)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { authenticated = person is not null, subjectId = person?.SubjectId, employeeNo = person?.EmployeeNo,
            displayName = person?.DisplayName, mustChangePassword = person?.MustChangePassword,
            permissions = person?.Permissions ?? [], csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken, serverTime = DateTimeOffset.UtcNow });
    }
    private static async Task<IResult> Complete(HttpContext http, IAntiforgery antiforgery, PersonnelMutation mutation)
    {
        if (mutation.Failure is { } failure)
        {
            if (mutation.RetryAfterSeconds > 0) http.Response.Headers.RetryAfter = mutation.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new RequestRejectedException(failure);
        }
        var grant = mutation.Grant ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, grant.Proof.SubjectId.ToString("D")), new Claim("svm.session", grant.Proof.SessionId.ToString("D")),
            new Claim("svm.proof", grant.Proof.Secret)], CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { ExpiresUtc = grant.ExpiresAt, IsPersistent = false, AllowRefresh = false });
        http.User = principal;
        // Antiforgery caches tokens in HttpContext; the earlier validation does not generate an anonymous request token.
        return View(http, antiforgery, grant.Person);
    }
}
