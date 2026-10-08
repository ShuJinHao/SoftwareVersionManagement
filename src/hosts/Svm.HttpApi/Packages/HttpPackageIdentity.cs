using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Svm.FileStorage;
using Svm.HttpApi.Personnel;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;

namespace Svm.HttpApi.Packages;

internal sealed class HttpPackageIdentity(IHttpContextAccessor accessor) : IPackageServiceIdentity, IPackageDownloadProof
{
    internal const string PeerItem = "svm.verified-package-peer";
    internal const string ResourceItem = "svm.package-internal-resource";
    private PackagePeer? Peer => accessor.HttpContext?.Items[PeerItem] as PackagePeer;
    public Guid SubjectId => Peer?.SubjectId ?? Guid.Empty;
    public string NodeId => Peer?.NodeId ?? "";
    public string Role => Peer?.Role ?? "";
    public SessionProof? Person => HttpPersonnelContext.Parse(accessor.HttpContext?.User);
    public AccessProof? Instance
    {
        get
        {
            var header = accessor.HttpContext?.Request.Headers.Authorization;
            if (header is null || header.Value.Count != 1 || header.Value[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
            var parts = value[7..].Split('.');
            return parts.Length == 2 && Guid.TryParseExact(parts[0], "D", out var id) && id != Guid.Empty && InstanceValidation.Secret(parts[1]) ? new(ActorKind.Instance, id, parts[1]) : null;
        }
    }
    internal static void Listen(KestrelServerOptions options, PackageFileOptions files, X509Certificate2 certificate) =>
        options.Listen(IPAddress.Parse(files.InternalListenAddress), files.InternalListenPort, listener => listener.UseHttps(certificate, https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) => files.Peers.Any(p => PackageFileOptions.Match(cert, p.CertificateSha256)) && files.Trust(cert);
        }));
    internal static async Task VerifyPeer(HttpContext http, Func<Task> next)
    {
        if (http.Request.Path.StartsWithSegments("/internal"))
        {
            var options = http.RequestServices.GetService<PackageFileOptions>() ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
            if (!http.Request.IsHttps || http.Connection.LocalPort != options.InternalListenPort) throw new RequestRejectedException(RequestFailure.PermissionDenied);
            var certificate = await http.Connection.GetClientCertificateAsync(http.RequestAborted) ?? throw new RequestRejectedException(RequestFailure.PermissionDenied);
            var peer = options.Peers.SingleOrDefault(p => PackageFileOptions.Match(certificate, p.CertificateSha256));
            if (peer is null || !options.Trust(certificate)) throw new RequestRejectedException(RequestFailure.PermissionDenied);
            http.Items[PeerItem] = peer;
        }
        await next();
    }
    internal static CallContextSnapshot? Context(HttpContext http)
    {
        if (http.Items[PeerItem] is not PackagePeer peer) return null;
        var resource = http.Items[ResourceItem] is Guid id && id != Guid.Empty ? id : throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        return new(new CallActor(ActorKind.Service, peer.SubjectId, workOwner: ModuleOwner.Packages, workId: resource), RequestKind.Internal, http.TraceIdentifier);
    }
}
