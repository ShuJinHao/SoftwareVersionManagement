using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.FileStorage;

public sealed class PackagePeerClient : IDisposable
{
    private readonly X509Certificate2 _client;
    private readonly X509Certificate2 _root;
    public HttpClient Client { get; }
    public PackagePeerClient(PackageFileOptions options, bool collector = false)
    {
        _client = collector ? PackageFileOptions.LoadCertificate(options.CollectorCertificatePath, options.CollectorCertificatePassword) : PackageFileOptions.LoadCertificate(options.ClientCertificatePath, options.ClientCertificatePassword);
        _root = new(options.TrustCertificatePath);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None };
        handler.ClientCertificates.Add(_client);
        handler.ServerCertificateCustomValidationCallback = (request, cert, _, errors) =>
        {
            if (cert is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || request.RequestUri is null) return false;
            var node = options.Nodes.SingleOrDefault(n => new Uri(n.InternalBaseUri).Authority == request.RequestUri.Authority);
            if (node is null || !PackageFileOptions.Match(cert, node.ServerCertificateSha256)) return false;
            using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(_root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); return chain.Build(cert);
        };
        Client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public void Dispose() { Client.Dispose(); _client.Dispose(); _root.Dispose(); }
}
internal sealed class PackageFiles(PackageFileOptions options, PackagePeerClient peer, IUnitOfWork unit, TimeProvider clock) : IPackageFiles
{
    public string NodeId => options.NodeId;
    public Task<IAsyncDisposable> LockUploadAsync(Guid uploadId, CancellationToken token) => Lock("upload-" + uploadId.ToString("D"), false, token);
    public async Task<(long Size, string Sha256)> ReceiveAsync(UploadReceipt r, Stream content, long? length,
        Func<CancellationToken, Task> verify, CancellationToken token)
    {
        Outside(); if (r.SourceNode != NodeId || r.ExpectedSize > options.Limits.MaxPackageBytes) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        if (length is { } declared && declared != r.ExpectedSize) throw new RequestRejectedException(RequestFailure.ChecksumMismatch);
        var temp = Location("staging", $"{r.UploadId:D}.{r.ReceiveToken:D}.partial");
        var result = await Write(temp, content, r.ExpectedSize, r.ExpectedSha256, verify, token);
        await verify(token);
        await Seal(temp, Location("staging", $"{r.UploadId:D}.verified"), r.ExpectedSize, r.ExpectedSha256, token);
        return result;
    }
    public async Task<IReadOnlyList<ReplicaFact>> PrepareReplicasAsync(PackageLease lease, Func<CancellationToken, Task> renew, CancellationToken token)
    {
        Outside(); var w = lease.Work;
        await using (await Lock("package-" + w.PackageId.ToString("D"), true, token))
        {
            var final = Location("replicas", $"{w.PackageId:D}.bin");
            if (!await Verified(final, w.ExpectedSize, w.ExpectedSha256, token))
            {
                var temp = Location("staging", $"{w.PackageId:D}.{lease.LeaseToken:D}.copy");
                if (w.SourceNode == NodeId)
                {
                    var source = w.Kind == "Repair" ? final : Location("staging", $"{w.WorkId:D}.verified");
                    if (!await Verified(source, w.ExpectedSize, w.ExpectedSha256, token)) throw new RequestRejectedException(RequestFailure.ChecksumMismatch);
                    await using var input = Open(source); await Write(temp, input, w.ExpectedSize, w.ExpectedSha256, renew, token);
                }
                else
                {
                    using var response = await peer.Client.GetAsync(Address(w.SourceNode, SourceRoute(lease)), HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode(); await using var input = await response.Content.ReadAsStreamAsync(token);
                    await Write(temp, input, w.ExpectedSize, w.ExpectedSha256, renew, token);
                }
                await renew(token); await Seal(temp, final, w.ExpectedSize, w.ExpectedSha256, token);
            }
        }
        foreach (var node in options.Nodes.Where(n => n.NodeId != NodeId))
        {
            await renew(token);
            using var request = new HttpRequestMessage(HttpMethod.Put, Address(node.NodeId, ReplicaRoute(lease)));
            request.Headers.ExpectContinue = true;
            request.Content = new StreamContent(Open(Location("replicas", $"{w.PackageId:D}.bin")));
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Content.Headers.ContentLength = w.ExpectedSize;
            using var response = await peer.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        }
        await renew(token); return await InspectAsync(w.PackageId, w.ExpectedSize, w.ExpectedSha256, token);
    }
    public async Task<IReadOnlyList<ReplicaFact>> InspectAsync(Guid packageId, long size, string sha256, CancellationToken token)
    {
        Outside(); return await Task.WhenAll(options.Nodes.Select(async n =>
        {
            if (n.NodeId == NodeId) return await InspectLocalAsync(packageId, size, sha256, token);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, Address(n.NodeId, $"internal/v1/package-replicas/{packageId:D}/integrity"));
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(options.Limits.UploadIdleSeconds));
                using var response = await peer.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                var valid = response.IsSuccessStatusCode && response.Headers.TryGetValues("X-Svm-Sha256", out var values) && values.SingleOrDefault() == sha256 &&
                    response.Content.Headers.ContentLength == size;
                return new ReplicaFact(n.NodeId, valid ? "Healthy" : "Missing", clock.GetUtcNow());
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); return new ReplicaFact(n.NodeId, "Missing", clock.GetUtcNow()); }
        }));
    }
    public async Task<ReplicaFact> InspectLocalAsync(Guid id, long size, string digest, CancellationToken token)
    {
        Outside(); var file = Location("replicas", $"{id:D}.bin");
        return new(NodeId, !File.Exists(file) ? "Missing" : await Verified(file, size, digest, token) ? "Healthy" : "Suspect", clock.GetUtcNow());
    }
    public Task<Stream> OpenReplicaAsync(Guid id, CancellationToken token)
    { Outside(); token.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(Open(Location("replicas", $"{id:D}.bin"))); }
    public Task<Stream> OpenSourceAsync(PackageWorkAuthority w, CancellationToken token)
    {
        Outside(); token.ThrowIfCancellationRequested();
        if (w.SourceNode != NodeId) throw new RequestRejectedException(RequestFailure.PermissionDenied);
        return Task.FromResult<Stream>(Open(Location(w.Kind == "Repair" ? "replicas" : "staging",
            w.Kind == "Repair" ? $"{w.PackageId:D}.bin" : $"{w.WorkId:D}.verified")));
    }
    public async Task ReceiveReplicaAsync(PackageWorkAuthority w, Stream content, Func<CancellationToken, Task> verify, CancellationToken token)
    {
        Outside(); await using var mutex = await Lock("package-" + w.PackageId.ToString("D"), true, token);
        var final = Location("replicas", $"{w.PackageId:D}.bin");
        await verify(token);
        // Healthy confirmed bytes are never overwritten, including by a late executor.
        if (await Verified(final, w.ExpectedSize, w.ExpectedSha256, token)) return;
        var temp = Location("staging", $"{w.PackageId:D}.{w.LeaseToken:D}.replica");
        await Write(temp, content, w.ExpectedSize, w.ExpectedSha256, verify, token);
        await verify(token); await Seal(temp, final, w.ExpectedSize, w.ExpectedSha256, token);
    }
    private async Task<(long Size, string Sha256)> Write(string temp, Stream input, long expected, string digest,
        Func<CancellationToken, Task> verify, CancellationToken token)
    {
        Outside(); await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        Private(temp); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[1024 * 1024]; long count = 0;
        var nextCheck = clock.GetUtcNow();
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(options.Limits.UploadIdleSeconds));
            var n = await input.ReadAsync(buffer, idle.Token); if (n == 0) break;
            count = checked(count + n); if (count > expected || count > options.Limits.MaxPackageBytes) throw new RequestRejectedException(RequestFailure.PayloadTooLarge);
            hash.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), token);
            if (clock.GetUtcNow() >= nextCheck) { await verify(token); nextCheck = clock.GetUtcNow().AddSeconds(5); }
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (count != expected || actual != digest) throw new RequestRejectedException(RequestFailure.ChecksumMismatch);
        await output.FlushAsync(token); output.Flush(flushToDisk: true); return (count, actual);
    }
    private async Task Seal(string temp, string target, long size, string sha, CancellationToken token)
    {
        if (File.Exists(target))
        {
            if (await Verified(target, size, sha, token)) { File.Delete(temp); return; }
            // Retain corrupt bytes for diagnosis, then install independently verified replacement bytes.
            File.Move(target, Location("quarantine", Path.GetFileName(target) + "." + Guid.NewGuid().ToString("D")));
        }
        File.Move(temp, target); Private(target);
    }
    private static async Task<bool> Verified(string file, long size, string digest, CancellationToken token)
    {
        if (!File.Exists(file) || new FileInfo(file).Length != size) return false;
        RejectLink(file); await using var stream = Open(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(digest, StringComparison.OrdinalIgnoreCase);
    }
    private async Task<IAsyncDisposable> Lock(string name, bool wait, CancellationToken token)
    {
        Outside(); var path = Location("locks", name + ".lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); Private(path); return file; }
            catch (IOException) { if (!wait) throw new RequestRejectedException(RequestFailure.UploadInProgress); await Task.Delay(100, token); }
        }
    }
    private string Location(string folder, string name)
    {
        Outside(); var root = Path.GetFullPath(options.RootPath); RejectTree(root); Directory.CreateDirectory(root);
        var dir = Path.Combine(root, folder); RejectTree(dir); Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(dir, name); RejectLink(path); return path;
    }
    private static void RejectTree(string path)
    { for (var info = new DirectoryInfo(path); info is not null; info = info.Parent) if (info.LinkTarget is not null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    private static void RejectLink(string path)
    { if (new FileInfo(path).LinkTarget is not null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    private static FileStream Open(string path) { RejectLink(path); return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); }
    private static void Private(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    private Uri Address(string node, string route) => new(new Uri(options.Nodes.Single(n => n.NodeId == node).InternalBaseUri), route);
    private static string SourceRoute(PackageLease l) => $"internal/v1/package-work/{l.Work.WorkId:D}/source?dispatch={l.Work.DispatchSequence}&leaseGeneration={l.LeaseGeneration}&leaseToken={l.LeaseToken:D}";
    private static string ReplicaRoute(PackageLease l) => $"internal/v1/package-replicas/{l.Work.PackageId:D}/content?workId={l.Work.WorkId:D}&dispatch={l.Work.DispatchSequence}&leaseGeneration={l.LeaseGeneration}&leaseToken={l.LeaseToken:D}";
    private void Outside() { if (unit.CurrentOperationId is not null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
