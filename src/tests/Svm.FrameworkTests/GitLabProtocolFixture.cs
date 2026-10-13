using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;

namespace Svm.FrameworkTests;

internal sealed class GitLabProtocolFixture : IAsyncDisposable
{
    private WebApplication? app;
    internal string Root { get; } = Path.Combine(OutboxFixture.Root, ".cache", "gitlab-protocol-" + Guid.NewGuid().ToString("N"));
    internal string Url { get; private set; } = "";
    internal string ApiToken { get; } = Guid.NewGuid().ToString("N"); internal string PackageToken { get; } = Guid.NewGuid().ToString("N");
    internal byte[] Bytes { get; } = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
    internal string Hash => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    internal string Mode { get; set; } = "valid"; internal string PipelineState { get; set; } = "success";
    internal List<string> Tags { get; } = ["v2.0.0"];
    internal int Downloads; internal bool CredentialLeak; internal bool InterruptOnce;
    internal GitLabOptions Options(Guid software) => new(new([new(Guid.NewGuid(), software, Guid.NewGuid(), Url, 17, "fixture")], PollSeconds: 30, PageSize: 2),
        [], Path.Combine(Root, "root.pem"));
    internal GitLabOptions Config(Guid software)
    { var o = Options(software); return o with { Credentials = [new(o.Import.Bindings[0].Id, ApiToken, PackageToken)] }; }
    internal GitLabReleaseFacts Facts(string tag = "v2.0.0") => GitLabImportTests.Fact(tag) with {
        Manifest = GitLabImportTests.Fact(tag).Manifest with { SizeBytes = Bytes.Length, Sha256 = Hash }, PipelineUrl = Url + "group/project/-/pipelines/3" };
    internal static async Task<GitLabProtocolFixture> StartAsync()
    {
        var f = new GitLabProtocolFixture(); Directory.CreateDirectory(f.Root);
        try
        {
            using var key = RSA.Create(2048); var req = new CertificateRequest("CN=GitLab owned fixture root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true)); req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1)); await File.WriteAllTextAsync(Path.Combine(f.Root, "root.pem"), ca.ExportCertificatePem());
            using var serverKey = RSA.Create(2048); var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); serverRequest.CertificateExtensions.Add(san.Build());
            using var publicCert = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(4), RandomNumberGenerator.GetBytes(16));
            using var cert = publicCert.CopyWithPrivateKey(serverKey); var path = Path.Combine(f.Root, "https.pfx"); var password = Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(path, cert.Export(X509ContentType.Pfx, password)); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); var port = PackageHostFixture.Port();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, l => l.UseHttps(path, password)));
            f.Url = $"https://127.0.0.1:{port}/"; f.app = builder.Build(); f.app.Map("/{**path}", f.Handle); await f.app.StartAsync(); return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    private async Task Handle(HttpContext http)
    {
        var path = http.Request.Path.Value!; var package = path.Contains("/packages/generic/", StringComparison.Ordinal);
        if (http.Request.Headers.ContainsKey(package ? "PRIVATE-TOKEN" : "DEPLOY-TOKEN")) CredentialLeak = true;
        if (Mode == "credential" || http.Request.Headers[package ? "DEPLOY-TOKEN" : "PRIVATE-TOKEN"] != (package ? PackageToken : ApiToken)) { http.Response.StatusCode = 401; return; }
        if (!path.StartsWith("/api/v4/projects/17/", StringComparison.Ordinal)) { http.Response.StatusCode = 404; return; }
        var tag = path.Contains("/releases/", StringComparison.Ordinal) || path.Contains("/repository/tags/", StringComparison.Ordinal) ? path.Split('/')[^1] : package ? path.Split('/')[^2] : Tags[0];
        if (path.EndsWith("/releases", StringComparison.Ordinal))
        {
            var page = int.Parse(http.Request.Query["page"]!); var count = int.Parse(http.Request.Query["per_page"]!);
            http.Response.Headers["X-Next-Page"] = page * count < Tags.Count ? (page + 1).ToString() : "";
            await http.Response.WriteAsJsonAsync(Tags.Skip((page - 1) * count).Take(count).Select(t => new { tag_name = t })); return;
        }
        var facts = Facts(tag); var m = facts.Manifest; var sha = Mode == "sha" ? new string('c', 40) : m.CommitSha;
        object pipeline = new { id = 3, project_id = Mode == "project" ? 18 : 17, sha, @ref = Mode == "pipeline-ref" ? "main" : tag, status = PipelineState, web_url = facts.PipelineUrl };
        if (path.EndsWith("/packages", StringComparison.Ordinal))
            await http.Response.WriteAsJsonAsync(new[] { new { id = 9, name = "fixture", version = http.Request.Query["package_version"].ToString(), package_type = "generic" } });
        else if (path.EndsWith("/package_files", StringComparison.Ordinal))
        {
            var entries = new[] { new { file_name = m.FileName, size = m.SizeBytes, file_sha256 = Mode == "registry-hash" ? new string('0', 64) : m.Sha256, pipelines = new[] { pipeline } },
                new { file_name = "release-manifest.json", size = 512L, file_sha256 = new string('a', 64), pipelines = Mode == "registry-pipeline" ? Array.Empty<object>() : new[] { pipeline } } };
            await http.Response.WriteAsJsonAsync(Mode == "registry-duplicate" ? entries.Concat(entries) : entries);
        }
        else if (path.Contains("/releases/", StringComparison.Ordinal))
        {
            string Link(string name) => Url + $"api/v4/projects/17/packages/generic/fixture/{tag}/{name}";
            await http.Response.WriteAsJsonAsync(new { tag_name = tag, name = facts.ReleaseName, released_at = facts.ReleasedAt, commit = new { id = m.CommitSha }, assets = new { links = new[] { new { url = Link("release-manifest.json") }, new { url = Mode == "asset" ? "https://foreign.invalid/package.zip" : Link(m.FileName) } } } });
        }
        else if (path.Contains("/repository/tags/", StringComparison.Ordinal)) await http.Response.WriteAsJsonAsync(new { name = tag, @protected = Mode != "unprotected", commit = new { id = sha } });
        else if (path.Contains("/pipelines/", StringComparison.Ordinal)) await http.Response.WriteAsJsonAsync(pipeline);
        else if (path.Contains("/jobs/", StringComparison.Ordinal)) await http.Response.WriteAsJsonAsync(new { id = Mode == "job" ? 5 : 4, status = "success", @ref = tag, tag = true, commit = new { id = sha }, pipeline });
        else if (path.EndsWith("/release-manifest.json", StringComparison.Ordinal))
        {
            if (Mode == "duplicate") await http.Response.WriteAsync("{\"schemaVersion\":9," + JsonSerializer.Serialize(m, new JsonSerializerOptions(JsonSerializerDefaults.Web))[1..]);
            else await http.Response.WriteAsJsonAsync(Mode == "manifest" ? m with { SchemaVersion = 9 } : m);
        }
        else if (package)
        {
            Interlocked.Increment(ref Downloads);
            if (Mode == "redirect") { http.Response.StatusCode = 302; http.Response.Headers.Location = "https://foreign.invalid/package.zip"; return; }
            http.Response.ContentLength = Bytes.Length;
            if (InterruptOnce) { InterruptOnce = false; await http.Response.Body.WriteAsync(Bytes.AsMemory(0, 100)); await http.Response.Body.FlushAsync(); http.Abort(); return; }
            var bytes = Bytes.ToArray(); if (Mode == "corrupt") bytes[0] ^= 1; await http.Response.Body.WriteAsync(bytes);
        }
        else http.Response.StatusCode = 404;
    }
    public async ValueTask DisposeAsync() { if (app is not null) await app.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    internal sealed class OutsideUnit : IUnitOfWork
    {
        public Guid? CurrentOperationId { get; set; }
        public Task<T> ExecuteAsync<T>(Guid id, Func<CancellationToken, Task<T>> action, CancellationToken token) => throw new NotSupportedException();
    }
}
