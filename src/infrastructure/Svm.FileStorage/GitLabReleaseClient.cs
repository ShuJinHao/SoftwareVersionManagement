using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;

namespace Svm.FileStorage;

/// <summary>Only constructed REST paths receive credentials. Redirects and release-supplied download addresses are never followed.</summary>
public sealed class GitLabReleaseClient : IGitLabReleaseClient, IDisposable
{
    private readonly GitLabOptions options;
    private readonly IUnitOfWork unit;
    private readonly HttpClient client;
    public GitLabReleaseClient(GitLabOptions options, IUnitOfWork unit) : this(options, unit, Transport(options)) { }
    private static HttpMessageHandler Transport(GitLabOptions options)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (options.TrustCertificatePath is not null) handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
            using var root = new X509Certificate2(options.TrustCertificatePath);
            using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.DisableCertificateDownloads = true; chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")); return chain.Build(certificate);
        };
        return handler;
    }
    internal GitLabReleaseClient(GitLabOptions options, IUnitOfWork unit, HttpMessageHandler transport)
    { this.options = options; this.unit = unit; client = new(transport) { Timeout = TimeSpan.FromSeconds(30) }; }
    public async Task<GitLabScanPage> ListAsync(GitLabBinding binding, int page, CancellationToken token)
    {
        if (page < 1 || page > options.Import.MaxPages) throw Invalid();
        using var response = await Get(binding, $"releases?order_by=released_at&sort=desc&per_page={options.Import.PageSize}&page={page}", false, token);
        using var json = await Json(response, token);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > options.Import.PageSize) throw Invalid();
        var tags = json.RootElement.EnumerateArray().Select(x => Text(x, "tag_name")).Where(t => GitLabVersions.TryParse(t, out _)).Distinct().ToArray();
        var next = response.Headers.TryGetValues("X-Next-Page", out var values) ? values.SingleOrDefault() : null;
        int? nextPage = string.IsNullOrEmpty(next) ? null : int.TryParse(next, out var n) && n == page + 1 && n <= options.Import.MaxPages ? n : throw Invalid();
        // GitLab returns an explicit pagination header, including an empty last page value.
        if (next is null && json.RootElement.GetArrayLength() == options.Import.PageSize) nextPage = page < options.Import.MaxPages ? page + 1 : throw Invalid();
        return new(tags, nextPage);
    }
    public async Task<GitLabReleaseFacts?> VerifyAsync(GitLabBinding binding, string tag, CancellationToken token)
    {
        try { return await VerifyCore(binding, tag, token); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { throw Invalid(); }
    }
    private async Task<GitLabReleaseFacts?> VerifyCore(GitLabBinding binding, string tag, CancellationToken token)
    {
        if (!GitLabVersions.TryParse(tag, out var version)) throw Invalid();
        using var releaseResponse = await Get(binding, "releases/" + Uri.EscapeDataString(tag), false, token);
        using var release = await Json(releaseResponse, token); var r = release.RootElement;
        using var tagResponse = await Get(binding, "repository/tags/" + Uri.EscapeDataString(tag), false, token);
        using var tagJson = await Json(tagResponse, token); var t = tagJson.RootElement;
        if (Text(r, "tag_name") != tag || Text(t, "name") != tag || !t.GetProperty("protected").GetBoolean()) throw Invalid();
        var sha = Text(t.GetProperty("commit"), "id");
        if (Text(r.GetProperty("commit"), "id") != sha) throw Invalid();
        var manifestPath = PackagePath(binding, tag, "release-manifest.json");
        var links = r.GetProperty("assets").GetProperty("links").EnumerateArray().Select(x => Text(x, "url")).ToArray();
        if (links.Count(x => x == Address(binding, manifestPath).AbsoluteUri) != 1) throw Invalid();
        using var manifestResponse = await Get(binding, manifestPath, true, token);
        using var manifestJson = await Json(manifestResponse, token);
        GitLabManifestV1 manifest;
        try { manifest = manifestJson.RootElement.Deserialize<GitLabManifestV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        if (!manifest.IsValid() || manifest.ProjectId != binding.ProjectId || manifest.Tag != tag || manifest.Version != version ||
            manifest.CommitSha != sha || manifest.PackageName != binding.PackageName || manifest.PackageVersion != tag ||
            links.Count(x => x == Address(binding, PackagePath(binding, tag, manifest.FileName)).AbsoluteUri) != 1) throw Invalid();
        using var pipelineResponse = await Get(binding, $"pipelines/{manifest.PipelineId}", false, token);
        using var pipeline = await Json(pipelineResponse, token); var p = pipeline.RootElement;
        if (p.GetProperty("id").GetInt64() != manifest.PipelineId || p.GetProperty("project_id").GetInt64() != binding.ProjectId ||
            Text(p, "sha") != sha || Text(p, "ref") != tag) throw Invalid();
        var status = Text(p, "status");
        if (status is "created" or "waiting_for_resource" or "preparing" or "pending" or "running" or "scheduled" or "manual") return null;
        if (status != "success") throw Invalid();
        using var jobResponse = await Get(binding, $"jobs/{manifest.JobId}", false, token);
        using var job = await Json(jobResponse, token); var j = job.RootElement; var jp = j.GetProperty("pipeline");
        if (j.GetProperty("id").GetInt64() != manifest.JobId || Text(j, "status") != "success" || Text(j, "ref") != tag || !j.GetProperty("tag").GetBoolean() ||
            Text(j.GetProperty("commit"), "id") != sha || jp.GetProperty("id").GetInt64() != manifest.PipelineId ||
            jp.GetProperty("project_id").GetInt64() != binding.ProjectId || Text(jp, "sha") != sha || Text(jp, "ref") != tag) throw Invalid();
        await VerifyRegistry(binding, manifest, token);
        var pipelineUrl = Text(p, "web_url");
        if (!Uri.TryCreate(pipelineUrl, UriKind.Absolute, out var uri) || !new Uri(binding.BaseUri).IsBaseOf(uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw Invalid();
        return new(manifest, Text(r, "name"), pipelineUrl, r.GetProperty("released_at").GetDateTimeOffset().ToUniversalTime());
    }
    public async Task<Stream> DownloadAsync(GitLabBinding binding, GitLabReleaseFacts release, CancellationToken token)
    {
        var m = release.Manifest;
        if (!m.IsValid() || m.ProjectId != binding.ProjectId || m.PackageName != binding.PackageName || m.PackageVersion != m.Tag) throw Invalid();
        var response = await Get(binding, PackagePath(binding, m.PackageVersion, m.FileName), true, token);
        if (response.Content.Headers.ContentLength is { } size && size != m.SizeBytes) { response.Dispose(); throw new RequestRejectedException(RequestFailure.ChecksumMismatch); }
        try { return new OwnedStream(await response.Content.ReadAsStreamAsync(token), response); } catch { response.Dispose(); throw; }
    }
    private async Task<HttpResponseMessage> Get(GitLabBinding binding, string path, bool package, CancellationToken token)
    {
        if (unit.CurrentOperationId is not null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        if (!options.Import.Bindings.Contains(binding) || !binding.Enabled) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        var credential = options.Credentials.Single(c => c.BindingId == binding.Id);
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(binding, path));
        request.Headers.Add(package ? "DEPLOY-TOKEN" : "PRIVATE-TOKEN", package ? credential.PackageToken : credential.ApiToken);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException) { throw new RequestRejectedException(RequestFailure.DependencyUnavailable); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode; response.Dispose();
        throw new RequestRejectedException(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? RequestFailure.CredentialInvalid :
            status == HttpStatusCode.NotFound ? RequestFailure.ResourceNotFound : RequestFailure.DependencyUnavailable);
    }
    private static Uri Address(GitLabBinding binding, string path) => new(new Uri(binding.BaseUri), $"api/v4/projects/{binding.ProjectId}/{path}");
    private static string PackagePath(GitLabBinding binding, string version, string name) => $"packages/generic/{Uri.EscapeDataString(binding.PackageName)}/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(name)}";
    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw Invalid();
    private static async Task<JsonDocument> Json(HttpResponseMessage response, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30)); token = timeout.Token;
        const int limit = 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw Invalid();
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var buffer = new MemoryStream(); var chunk = new byte[8192];
        while (true) { var read = await stream.ReadAsync(chunk, token); if (read == 0) break; if (buffer.Length + read > limit) throw Invalid(); buffer.Write(chunk, 0, read); }
        try
        {
            var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            try { UniqueProperties(json.RootElement); return json; } catch { json.Dispose(); throw; }
        }
        catch (JsonException) { throw Invalid(); }
    }
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); UniqueProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }
    private async Task VerifyRegistry(GitLabBinding binding, GitLabManifestV1 manifest, CancellationToken token)
    {
        long? packageId = null;
        await Pages(binding, $"packages?package_type=generic&package_name={Uri.EscapeDataString(binding.PackageName)}&package_version={Uri.EscapeDataString(manifest.Tag)}&status=default", item =>
        {
            if (Text(item, "name") != binding.PackageName || Text(item, "version") != manifest.Tag || Text(item, "package_type") != "generic") return;
            if (packageId is not null) throw Invalid(); packageId = item.GetProperty("id").GetInt64();
        }, token);
        if (packageId is null or <= 0) throw Invalid();
        var packageCount = 0; var manifestCount = 0;
        await Pages(binding, $"packages/{packageId}/package_files?", item =>
        {
            var name = Text(item, "file_name"); if (name != manifest.FileName && name != "release-manifest.json") return;
            if (name == manifest.FileName)
            {
                if (++packageCount != 1 || item.GetProperty("size").GetInt64() != manifest.SizeBytes ||
                    !string.Equals(Text(item, "file_sha256"), manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            }
            else if (++manifestCount != 1) throw Invalid();
            if (!item.GetProperty("pipelines").EnumerateArray().Any(p => p.GetProperty("id").GetInt64() == manifest.PipelineId &&
                Text(p, "sha") == manifest.CommitSha && Text(p, "ref") == manifest.Tag)) throw Invalid();
        }, token);
        if (packageCount != 1 || manifestCount != 1) throw Invalid();
    }
    private async Task Pages(GitLabBinding binding, string path, Action<JsonElement> visit, CancellationToken token)
    {
        for (var page = 1; page <= options.Import.MaxPages; page++)
        {
            using var response = await Get(binding, path + $"&per_page=100&page={page}", false, token);
            using var json = await Json(response, token);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 100) throw Invalid();
            foreach (var item in json.RootElement.EnumerateArray()) visit(item);
            if (response.Headers.TryGetValues("X-Next-Page", out var values))
            {
                var next = values.SingleOrDefault(); if (string.IsNullOrEmpty(next)) return;
                if (!int.TryParse(next, out var n) || n != page + 1) throw Invalid();
            }
            else if (json.RootElement.GetArrayLength() < 100) return;
        }
        throw Invalid();
    }
    private static RequestRejectedException Invalid() => new(RequestFailure.ValidationFailed);
    public void Dispose() => client.Dispose();
    private sealed class OwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
    }
}
