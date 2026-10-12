using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Svm.EntityFrameworkCore.Migrations;
using Svm.EventBus;
using Svm.FileStorage;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Packages;
using Xunit;
namespace Svm.FrameworkTests;

// Owns only this random database/vhost, two logical file roots, certificates, API/Worker processes and gateway.
internal sealed class PackageHostFixture : IAsyncDisposable
{
    internal PersonnelDatabase Personnel { get; private set; } = null!;
    internal OutboxBroker Broker { get; } = new();
    internal SiteCatalogOptions Site { get; } = new(Guid.NewGuid(), "版本安装包夹具厂区", "Asia/Shanghai");
    internal PackageFileOptions A { get; private set; } = null!;
    internal PackageFileOptions B { get; private set; } = null!;
    internal LocalApi ApiA { get; private set; } = null!;
    internal LocalApi ApiB { get; private set; } = null!;
    internal string DirectoryPath { get; } = Path.Combine(OutboxFixture.Root, ".cache", "package-verification-" + Guid.NewGuid().ToString("N"));
    internal PackageCopyProxy? CopyProxy { get; private set; }
    internal string GatewayUrl { get; private set; } = "";
    internal string CertificatePassword { get; } = Guid.NewGuid().ToString("N");
    internal MessagingOptions Messaging => Broker.Options with { SiteId = Site.Require().SiteId, QueryDelaySeconds = 1 };
    internal int ApiPortA { get; private set; } internal int ApiPortB { get; private set; }
    private readonly List<PackageProcess> _workers = []; private readonly List<string> _files = [];
    private readonly Dictionary<string, string> _gatewayThumbprints = new();
    private string? _container; private bool _brokerReady; private Guid _generationA = Guid.NewGuid(), _generationB = Guid.NewGuid();
    internal static async Task<PackageHostFixture> CreateAsync(bool copyProxy = false)
    {
        var f = new PackageHostFixture();
        try
        {
            Directory.CreateDirectory(f.DirectoryPath); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Personnel = await PersonnelDatabase.CreateAsync(); await new MigrationRunner(MigrationConfiguration.Create(f.Personnel.Database.MigrationConnection, f.Personnel.Database.WriterRole, f.Personnel.Database.ReaderRole, true)).ApplyAsync(default);
            await f.Broker.InitializeAsync(); f._brokerReady = true; f.CreateCertificates();
            var pa = Port(); var pb = Port(); f.ApiPortA = Port(); f.ApiPortB = Port();
            if (copyProxy) f.CopyProxy = new(f.CertPath("server-node-b", ".pfx"), f.CertPath("peer-node-a", ".pfx"), f.CertificatePassword, pb);
            var nodes = new[] { f.Node("node-a", pa), f.Node("node-b", f.CopyProxy?.Port ?? pb) }; var peers = f.Peers();
            f.A = f.Options("node-a", pa, nodes, peers); f.B = f.Options("node-b", pb, nodes, peers);
            f.ApiA = await LocalApi.StartAsync(f.Personnel.Database, site: f.Site, instanceAccess: InstanceFixture.Limits, packages: f.A, messaging: f.Messaging, publicPort: f.ApiPortA);
            f.ApiB = await LocalApi.StartAsync(f.Personnel.Database, site: f.Site, instanceAccess: InstanceFixture.Limits, packages: f.B, messaging: f.Messaging, publicPort: f.ApiPortB);
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal async Task<PackageProcess> WorkerAsync(PackageFileOptions options)
    { var worker = await PackageProcess.StartAsync(this, options); _workers.Add(worker); return worker; }
    internal async Task RestartApiBAsync()
    { await ApiB.DisposeAsync(); ApiB = await LocalApi.StartAsync(Personnel.Database, site: Site, instanceAccess: InstanceFixture.Limits, packages: B, messaging: Messaging, publicPort: ApiPortB); }
    internal HttpClient Client(CookieContainer cookies) => ApiA.Client(cookies);
    internal string Replica(PackageFileOptions node, Guid id) => Path.Combine(node.RootPath, "replicas", id.ToString("D") + ".bin");
    internal static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }
    private string CertPath(string name, string extension) => Path.Combine(DirectoryPath, "certs", name + extension);
    private void Write(string path, string contents) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    private void CreateCertificates()
    {
        using var rootKey = RSA.Create(2048); var rootRequest = new CertificateRequest("CN=SVM owned package verification root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true)); rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var issuer = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3)); Write(CertPath("root", ".pem"), issuer.ExportCertificatePem());
        foreach (var node in new[] { "node-a", "node-b" }) foreach (var role in new[] { "server", "peer", "gateway", "collector" })
        {
            using var key = RSA.Create(2048); var request = new CertificateRequest("CN=" + role + "-" + node, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true)); request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(role == "server" ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
            if (role == "server") { var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddDnsName("host.docker.internal"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build()); }
            using var publicCertificate = request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16)); using var certificate = publicCertificate.CopyWithPrivateKey(key);
            var name = role + "-" + node; File.WriteAllBytes(CertPath(name, ".pfx"), certificate.Export(X509ContentType.Pfx, CertificatePassword)); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(CertPath(name, ".pfx"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Write(CertPath(name, ".pem"), certificate.ExportCertificatePem()); Write(CertPath(name, ".key"), key.ExportPkcs8PrivateKeyPem());
            if (role == "gateway") _gatewayThumbprints[node] = string.Join(':', certificate.GetCertHashString().Chunk(2).Select(c => new string(c)));
        }
    }
    private PackageNode Node(string node, int port) { using var c = new X509Certificate2(CertPath("server-" + node, ".pfx"), CertificatePassword); return new(node, $"https://127.0.0.1:{port}/", c.GetCertHashString(HashAlgorithmName.SHA256)); }
    private PackagePeer[] Peers() => new[] { "node-a", "node-b" }.SelectMany(n => new[] { "Peer", "Gateway", "Collector" }.Select(r => { using var c = new X509Certificate2(CertPath(r.ToLowerInvariant() + "-" + n, ".pfx"), CertificatePassword); return new PackagePeer(c.GetCertHashString(HashAlgorithmName.SHA256), n, Guid.NewGuid(), r); })).ToArray();
    private PackageFileOptions Options(string n, int port, PackageNode[] nodes, PackagePeer[] peers)
    {
        var log = Path.Combine(DirectoryPath, n, "download-logs"); Directory.CreateDirectory(log);
        return new(n, Path.Combine(DirectoryPath, n, "files"), Guid.NewGuid(), "0.0.0.0", port, CertPath("server-" + n, ".pfx"), CertificatePassword,
            CertPath("peer-" + n, ".pfx"), CertificatePassword, CertPath("root", ".pem"), CertPath("collector-" + n, ".pfx"), CertificatePassword, log, new(32 * 1024 * 1024, 5), new(15, 1, 5, 20), nodes, peers);
    }
    internal async Task GatewayAsync()
    {
        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(OutboxFixture.Root, "build/nginx.packages.json"))).RootElement.Clone();
        var config = new { workerUser = "root", certificateDirectory = "/svm/certs", gatewayFingerprints = _gatewayThumbprints.Values.ToArray(), nodes = new[] {
            new { nodeId = A.NodeId, generation = _generationA, publicPort = 10443, privatePort = 20443, apiPublicPort = ApiPortA, apiInternalPort = A.InternalListenPort, rootPath = "/svm/node-a/files", logDirectory = "/svm/node-a/download-logs", maxPackageBytes = A.Limits.MaxPackageBytes, uploadIdleSeconds = A.Limits.UploadIdleSeconds },
            new { nodeId = B.NodeId, generation = _generationB, publicPort = 11443, privatePort = 21443, apiPublicPort = ApiPortB, apiInternalPort = B.InternalListenPort, rootPath = "/svm/node-b/files", logDirectory = "/svm/node-b/download-logs", maxPackageBytes = B.Limits.MaxPackageBytes, uploadIdleSeconds = B.Limits.UploadIdleSeconds } } };
        var input = Path.Combine(DirectoryPath, "nginx-input.json"); Write(input, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var conf = Path.Combine(DirectoryPath, "nginx.conf"); await Command(Path.Combine(OutboxFixture.Root, ".tools/node/bin/node"), [Path.Combine(OutboxFixture.Root, "eng/packages/nginx.mjs"), input, conf]);
        _container = "svm-package-nginx-" + Guid.NewGuid().ToString("N"); var port = Port(); var image = manifest.GetProperty("image").GetString()!;
        var inspected = await Command("docker", ["--context", "desktop-linux", "image", "inspect", image], optional: true);
        if (inspected.Exit != 0) await Command("docker", ["--context", "desktop-linux", "pull", "--platform", "linux/arm64", image]);
        await Command("docker", ["--context", "desktop-linux", "run", "--detach", "--name", _container, "--label", "io.svm.package-test=" + Path.GetFileName(DirectoryPath), "--platform", "linux/arm64", "--publish", $"127.0.0.1:{port}:10443", "--mount", "type=bind,src=" + DirectoryPath + ",dst=/svm", "--mount", "type=bind,src=" + conf + ",dst=/etc/nginx/nginx.conf,readonly", image]);
        var valid = await Command("docker", ["--context", "desktop-linux", "exec", _container, "nginx", "-t"], optional: true);
        if (valid.Exit != 0) { var diagnostic = await Command("docker", ["--context", "desktop-linux", "logs", _container], optional: true); throw new InvalidOperationException("Owned Nginx validation failed: " + diagnostic.Output); }
        var compiled = await Command("docker", ["--context", "desktop-linux", "exec", _container, "nginx", "-V"]); Assert.Contains("--with-http_auth_request_module", compiled.Output);
        GatewayUrl = $"https://127.0.0.1:{port}"; using var client = Client(new()); await OutboxFixture.WaitAsync(async () => { try { using var r = await client.GetAsync(GatewayUrl + "/api/v1/session"); return r.IsSuccessStatusCode; } catch { return false; } }, 15);
    }
    internal static async Task<(int Exit, string Output)> Command(string command, IReadOnlyList<string> args, bool optional = false)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo(command) { WorkingDirectory = OutboxFixture.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } }; foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg); Assert.True(p.Start()); var a = p.StandardOutput.ReadToEndAsync(); var b = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); } finally { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(); } }
        var output = await a + await b; if (!optional && p.ExitCode != 0) throw new InvalidOperationException("Owned package verification command failed: " + output); return (p.ExitCode, output);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var w in _workers) await w.DisposeAsync();
        if (CopyProxy is not null) await CopyProxy.DisposeAsync();
        if (_container is not null) { await Command("docker", ["--context", "desktop-linux", "rm", "--force", _container], optional: true); _container = null; }
        if (ApiA is not null) { ApiA.AssertRedacted(CertificatePassword); await ApiA.DisposeAsync(); }
        if (ApiB is not null) { ApiB.AssertRedacted(CertificatePassword); await ApiB.DisposeAsync(); }
        if (_brokerReady) { await Broker.DisposeAsync(); _brokerReady = false; }
        if (Personnel is not null) await Personnel.DisposeAsync(); foreach (var file in _files) File.Delete(file);
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}
internal sealed class PackageProcess : IAsyncDisposable
{
    private readonly Process _process; private readonly string[] _configs; private readonly Task<string> _errors; private readonly StringBuilder _logs = new(); private readonly string[] _secrets; private bool _disposed;
    private PackageProcess(Process p, string[] configs, string[] secrets) { _process = p; _configs = configs; _secrets = secrets; _errors = p.StandardError.ReadToEndAsync(); }
    internal static async Task<PackageProcess> StartAsync(PackageHostFixture f, PackageFileOptions options)
    {
        var configs = new[] { await OutboxFixture.PrivateJsonAsync(new { writerConnectionString = f.Personnel.Database.WriterConnection, readerConnectionString = f.Personnel.Database.ReaderConnection }), await OutboxFixture.PrivateJsonAsync(f.Messaging), await OutboxFixture.PrivateJsonAsync(f.Site), await OutboxFixture.PrivateJsonAsync(options) };
        var info = new ProcessStartInfo(Path.Combine(OutboxFixture.Root, "eng/dotnet")) { WorkingDirectory = OutboxFixture.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; info.ArgumentList.Add(Path.Combine(OutboxFixture.Root, "src/hosts/Svm.Worker/bin/Debug/net8.0/Svm.Worker.dll"));
        foreach (var (name, i) in new[] { ("SVM_PERSISTENCE_CONFIG_FILE", 0), ("SVM_MESSAGING_CONFIG_FILE", 1), ("SVM_SITE_CONFIG_FILE", 2), ("SVM_PACKAGE_CONFIG_FILE", 3) }) info.Environment[name] = configs[i]; info.Environment["Logging__LogLevel__Default"] = "Information"; info.Environment["Logging__LogLevel__MassTransit"] = "Warning";
        var p = new Process { StartInfo = info, EnableRaisingEvents = true }; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); PackageProcess? worker = null;
        p.Exited += (_, _) => ready.TrySetException(new InvalidOperationException("Package Worker exited before readiness; redacted log retained."));
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (worker!._logs) worker._logs.AppendLine(e.Data); if (e.Data.Contains("Application started.", StringComparison.Ordinal)) ready.TrySetResult(); };
        try { Assert.True(p.Start()); worker = new(p, configs, [f.CertificatePassword, f.Messaging.Password]); p.BeginOutputReadLine(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(25)); Assert.False(p.HasExited); return worker; }
        catch { if (worker is not null) await worker.DisposeAsync(); else { foreach (var c in configs) File.Delete(c); p.Dispose(); } throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; if (!_process.HasExited) _process.Kill(true); await _process.WaitForExitAsync(); var output = _logs.ToString() + await _errors; foreach (var secret in _secrets) PersistenceDatabase.AssertRedacted(output, secret);
        var directory = Path.Combine(OutboxFixture.Root, "artifacts", "releases-packages"); Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "worker-" + Guid.NewGuid().ToString("N") + ".log"), output); foreach (var c in _configs) File.Delete(c); _process.Dispose();
    }
}
