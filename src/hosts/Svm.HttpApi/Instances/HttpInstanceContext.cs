using Svm.HttpApi.Personnel;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;

namespace Svm.HttpApi.Instances;

internal sealed class HttpAccessProofSource(IHttpContextAccessor accessor) : IAccessProofSource
{
    public AccessProof? Proof
    {
        get
        {
            var http=accessor.HttpContext;
            var kind=http?.GetEndpoint()?.Metadata.GetMetadata<PersonnelEndpointKind>()?.Kind;
            var actor=kind switch { RequestKind.Client=>ActorKind.Instance,RequestKind.Enrollment=>ActorKind.EnrollmentGrant,RequestKind.Recovery=>ActorKind.RecoveryGrant,_=>ActorKind.Anonymous };
            if(actor==ActorKind.Anonymous || http is null || !http.Request.Headers.TryGetValue("Authorization",out var header) || header.Count!=1) return null;
            var text=header[0];
            if(text is null || text.Length>200 || !text.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)) return null;
            var parts=text[7..].Split('.');
            return parts.Length==2 && Guid.TryParseExact(parts[0],"D",out var id) && id!=Guid.Empty && InstanceValidation.Secret(parts[1]) ? new(actor,id,parts[1]) : null;
        }
    }
    internal static bool IsMachine(HttpContext http) => http.GetEndpoint()?.Metadata.GetMetadata<PersonnelEndpointKind>()?.Kind is RequestKind.Client or RequestKind.Enrollment or RequestKind.Recovery;
    internal const string IdentityItem="svm.verified-instance-identity";
    internal static async Task Authenticate(HttpContext http,Func<Task> next)
    {
        if(IsMachine(http))
        {
            if(!http.Request.IsHttps) throw new RequestRejectedException(RequestFailure.PermissionDenied);
            var source=http.RequestServices.GetRequiredService<IAccessProofSource>();
            var proof=source.Proof ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid);
            var identity=await http.RequestServices.GetRequiredService<IInstanceAccess>().AuthenticateAsync(proof,false,http.RequestAborted)
                ?? throw new RequestRejectedException(RequestFailure.CredentialInvalid);
            http.Items[IdentityItem]=identity;
        }
        else if(http.Request.Path.StartsWithSegments("/api") && http.Request.Headers.ContainsKey("Authorization"))
            throw new RequestRejectedException(RequestFailure.CredentialInvalid);
        await next();
    }
}
