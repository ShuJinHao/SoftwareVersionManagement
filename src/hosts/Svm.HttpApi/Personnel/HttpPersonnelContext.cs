using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.HttpApi.Personnel;

internal sealed record PersonnelEndpointKind(RequestKind Kind);

internal sealed class HttpPersonnelContext(IHttpContextAccessor accessor) : ITrustedCallContextSource, ISessionProofSource
{
    public SessionProof? Proof => Parse(accessor.HttpContext?.User);
    // Forwarded headers are not enabled. Only the actual peer is trusted in the local verification profile.
    public string SourceAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "local-test-transport";
    public CallContextSnapshot? GetCurrent()
    {
        var http = accessor.HttpContext;
        if (http is null) return null;
        return new(Proof is { } proof ? new CallActor(ActorKind.Human, proof.SubjectId) : new CallActor(ActorKind.Anonymous),
            http.GetEndpoint()?.Metadata.GetMetadata<PersonnelEndpointKind>()?.Kind ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid), http.TraceIdentifier);
    }
    internal static SessionProof? Parse(ClaimsPrincipal? principal) => principal?.Identity?.IsAuthenticated == true &&
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var subject) && subject != Guid.Empty &&
        Guid.TryParse(principal.FindFirstValue("svm.session"), out var session) && session != Guid.Empty &&
        principal.FindFirstValue("svm.proof") is { Length: 64 } secret ? new(session, subject, secret) : null;
}

internal sealed class PersonnelCookieEvents(IPersonnelService personnel) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            context.RejectPrincipal();
            return;
        }
        var proof = HttpPersonnelContext.Parse(context.Principal);
        if (proof is null || await personnel.AuthenticateAsync(proof, false, context.HttpContext.RequestAborted) is null)
            throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
    }
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        throw new RequestRejectedException(RequestFailure.AuthenticationRequired);
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) =>
        throw new RequestRejectedException(RequestFailure.PermissionDenied);
}
