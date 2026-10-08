using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Framework;

namespace Svm.FileStorage;

public sealed record PackageNode(string NodeId, string InternalBaseUri, string ServerCertificateSha256);
public sealed record PackagePeer(string CertificateSha256, string NodeId, Guid SubjectId, string Role);
public sealed record PackageFileOptions(string NodeId, string RootPath, Guid ServiceSubjectId,
    string InternalListenAddress, int InternalListenPort, string ServerCertificatePath, string? ServerCertificatePassword,
    string ClientCertificatePath, string? ClientCertificatePassword, string TrustCertificatePath,
    string CollectorCertificatePath, string? CollectorCertificatePassword, string DownloadLogDirectory,
    PackageLimits Limits, PackageExecutionOptions Execution, IReadOnlyList<PackageNode> Nodes, IReadOnlyList<PackagePeer> Peers)
{
    public override string ToString() => "PackageFileOptions [private configuration redacted]";
    public void Validate()
    {
        Limits.Validate(); Execution.Validate(); new PackageNodeCatalog(Nodes.Select(x => x.NodeId).ToArray()).Validate();
        if (!Nodes.Any(x => x.NodeId == NodeId) || ServiceSubjectId == Guid.Empty ||
            !IPAddress.TryParse(InternalListenAddress, out _) || InternalListenPort is < 1024 or > 65535 ||
            !Absolute(RootPath) || !Absolute(DownloadLogDirectory) || !Absolute(ServerCertificatePath) || !Absolute(ClientCertificatePath) ||
            !Absolute(CollectorCertificatePath) || !Absolute(TrustCertificatePath) || Peers.Count is < 4 or > 32 ||
            Peers.Select(x => x.CertificateSha256.ToUpperInvariant()).Distinct().Count() != Peers.Count ||
            Peers.Any(x => !Fingerprint(x.CertificateSha256) || !Nodes.Any(n => n.NodeId == x.NodeId) || x.SubjectId == Guid.Empty ||
                x.Role is not ("Peer" or "Gateway" or "Collector")) ||
            Nodes.Any(n => !Fingerprint(n.ServerCertificateSha256) || !Uri.TryCreate(n.InternalBaseUri, UriKind.Absolute, out var u) ||
                u.Scheme != "https" || u.UserInfo.Length != 0 || u.AbsolutePath != "/" || u.Query.Length != 0 || u.Fragment.Length != 0))
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        try
        {
            using var server = LoadCertificate(ServerCertificatePath, ServerCertificatePassword);
            using var client = LoadCertificate(ClientCertificatePath, ClientCertificatePassword);
            using var collector = LoadCertificate(CollectorCertificatePath, CollectorCertificatePassword);
            using var trust = new X509Certificate2(TrustCertificatePath);
            if (!server.HasPrivateKey || !client.HasPrivateKey || !collector.HasPrivateKey || trust.HasPrivateKey ||
                server.NotAfter.ToUniversalTime() <= DateTime.UtcNow || client.NotAfter.ToUniversalTime() <= DateTime.UtcNow || collector.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
                !Match(server, Nodes.Single(n => n.NodeId == NodeId).ServerCertificateSha256) ||
                !Peers.Any(p => p.NodeId == NodeId && p.Role == "Peer" && Match(client, p.CertificateSha256)) ||
                !Peers.Any(p => p.NodeId == NodeId && p.Role == "Collector" && Match(collector, p.CertificateSha256)))
                throw new InvalidOperationException();
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
    public static PackageFileOptions? LoadFromEnvironment() => Load(Environment.GetEnvironmentVariable("SVM_PACKAGE_CONFIG_FILE"));
    public static PackageFileOptions? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Absolute(path) || !File.Exists(path)) throw new InvalidOperationException();
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new InvalidOperationException();
            var options = JsonSerializer.Deserialize<PackageFileOptions>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidOperationException();
            options.Validate(); return options;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
    private static bool Absolute(string x) => !string.IsNullOrWhiteSpace(x) && Path.IsPathFullyQualified(x) && !x.Any(char.IsControl);
    private static bool Fingerprint(string x) => x is { Length: 64 } && x.All(char.IsAsciiHexDigit);
    public static X509Certificate2 LoadCertificate(string path, string? password) => new(path, password,
        OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
    public static bool Match(X509Certificate2 certificate, string sha256) =>
        string.Equals(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), sha256, StringComparison.OrdinalIgnoreCase);
    public bool Trust(X509Certificate2 certificate)
    {
        using var root = new X509Certificate2(TrustCertificatePath);
        using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(certificate);
    }
}
public static class PackageFileRegistration
{
    public static IServiceCollection AddSvmPackageFiles(this IServiceCollection services, PackageFileOptions options)
    {
        options.Validate(); services.AddSingleton(options); services.AddSingleton(options.Limits); services.AddSingleton(options.Execution);
        services.AddSingleton(new PackageNodeCatalog(options.Nodes.Select(x => x.NodeId).ToArray()));
        services.AddSingleton<PackagePeerClient>(); services.AddScoped<IPackageFiles, PackageFiles>(); services.AddScoped<IPackageDownloadLogPump, PackageDownloadLogPump>(); return services;
    }
}
