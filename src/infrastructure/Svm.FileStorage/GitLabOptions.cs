using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;

namespace Svm.FileStorage;

public sealed record GitLabCredential(Guid BindingId, string ApiToken, string PackageToken)
{ public override string ToString() => "GitLabCredential [redacted]"; }
public sealed record GitLabConnectionCredential(Guid ConnectionId, string ApiToken, string PackageToken)
{ public override string ToString() => "GitLabConnectionCredential [redacted]"; }
public sealed record GitLabOptions(GitLabImportOptions Import, IReadOnlyList<GitLabCredential> Credentials, string? TrustCertificatePath = null, IReadOnlyList<GitLabConnectionCredential>? ConnectionCredentials = null)
{
    public override string ToString() => "GitLabOptions [private configuration redacted]";
    public GitLabCredential Credential(GitLabBinding binding)
    {
        if (!Import.IsAvailable(binding)) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        if (binding.ConnectionId is { } connectionId)
        {
            var value = (ConnectionCredentials ?? []).Single(c => c.ConnectionId == connectionId);
            return new(binding.Id, value.ApiToken, value.PackageToken);
        }
        return Credentials.Single(c => c.BindingId == binding.Id);
    }
    public void Validate()
    {
        if (TrustCertificatePath is not null)
        {
            if (!Path.IsPathFullyQualified(TrustCertificatePath) || !File.Exists(TrustCertificatePath)) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
            try { using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(TrustCertificatePath); if (certificate.HasPrivateKey) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
            catch (System.Security.Cryptography.CryptographicException) { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
        }
        var sources = Import.Sources; var secrets = ConnectionCredentials ?? [];
        if (sources.Count > 1000 || sources.Select(c => c.Id).Distinct().Count() != sources.Count ||
            secrets.Count != sources.Count || secrets.Select(c => c.ConnectionId).Distinct().Count() != secrets.Count ||
            sources.Sum(c => c.Projects.Sum(p => p.Packages.Count)) > 10000 ||
            sources.Any(c => c.Id == Guid.Empty || c.Name is not { Length: > 0 and <= 200 } || !Address(c.BaseUri) ||
                c.Projects.Count is < 1 or > 1000 || c.Projects.Select(p => p.Id).Distinct().Count() != c.Projects.Count ||
                c.Projects.Any(p => p.Id <= 0 || p.Name is not { Length: > 0 and <= 200 } || p.Packages.Count is < 1 or > 1000 ||
                    p.Packages.Distinct(StringComparer.Ordinal).Count() != p.Packages.Count || p.Packages.Any(n => !GitLabVersions.Segment(n))) ||
                !secrets.Any(k => k.ConnectionId == c.Id && Secret(k.ApiToken) && Secret(k.PackageToken))))
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        if (Import.Bindings.Count > 1000 || Import.Bindings.Count == 0 && Import.Sources.Count == 0 || Import.PollSeconds is < 30 or > 86400 || Import.PageSize is < 1 or > 100 ||
            Import.MaxPages is < 1 or > 10000 || Import.LeaseSeconds is < 60 or > 3600 ||
            Import.Bindings.Select(b => b.Id).Distinct().Count() != Import.Bindings.Count ||
            Import.Bindings.Select(b => b.SoftwareId).Distinct().Count() != Import.Bindings.Count ||
            Import.Bindings.Select(b => b.SubjectId).Distinct().Count() != Import.Bindings.Count ||
            Credentials.Count != Import.Bindings.Count || Credentials.Select(c => c.BindingId).Distinct().Count() != Credentials.Count ||
            Import.Bindings.Any(b => b.ConnectionId is not null || b.SourceRevision != 1 || b.Id == Guid.Empty || b.SoftwareId == Guid.Empty || b.SubjectId == Guid.Empty || b.ProjectId <= 0 ||
                !GitLabVersions.Segment(b.PackageName) || !Uri.TryCreate(b.BaseUri, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !b.BaseUri.EndsWith('/') ||
                !Credentials.Any(c => c.BindingId == b.Id && Secret(c.ApiToken) && Secret(c.PackageToken))))
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    private static bool Address(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && value.EndsWith('/');
    private static bool Secret(string? value) => value is { Length: >= 8 and <= 4096 } && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);
    public static GitLabOptions? LoadFromEnvironment() => Load(Environment.GetEnvironmentVariable("SVM_GITLAB_CONFIG_FILE"));
    public static GitLabOptions? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024 ||
                !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new InvalidOperationException();
            var options = JsonSerializer.Deserialize<GitLabOptions>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidOperationException();
            options.Validate(); return options;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
}
public static class GitLabRegistration
{
    public static IServiceCollection AddSvmGitLab(this IServiceCollection services, GitLabOptions options)
    { options.Validate(); return services.AddSingleton(options).AddSingleton(options.Import).AddScoped<IGitLabReleaseClient, GitLabReleaseClient>(); }
}
