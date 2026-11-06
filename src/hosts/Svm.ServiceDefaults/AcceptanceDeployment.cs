using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Svm.ServiceDefaults;

/// <summary>Offline technical initialization only. Never connects to a database or starts a service.</summary>
public static class AcceptanceDeployment
{
    private const UnixFileMode FileModePrivate = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryModePrivate = FileModePrivate | UnixFileMode.UserExecute;
    public sealed record Result(string Project, Guid DeploymentId, string CandidateId);

    public static async Task<Result> InitializeAsync(string settingsPath, string bundle, string output, CancellationToken token)
    {
        var settings = await AcceptanceSettings.LoadAsync(settingsPath, token);
        var manifest = await CandidateBundle.VerifyAsync(bundle, token);
        output = DeploymentBackup.SafeRoot(output); bundle = DeploymentBackup.SafeRoot(bundle);
        if (output == bundle || output.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal) || bundle.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Private output must be separate from public bundle.");
        if (File.Exists(output) || Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Initialization never overwrites existing content.");
        PrivateDirectory(output);
        await WriteAsync(output, ".initializing", "Incomplete initialization; do not start services.\n", token);
        var id = Guid.NewGuid(); var project = "svm-site-" + id.ToString("N")[..12];
        var password = Secret(); var initialPassword = Secret() + "!9aA";
        var databaseSecrets = new Dictionary<string, string> { ["bootstrap"] = Secret(), ["migrate"] = Secret(), ["app"] = Secret(), ["read"] = Secret() };
        var brokerSecret = Secret(); var brokerVhost = "svm-" + id;
        var start = DateTimeOffset.UtcNow.AddMinutes(-5); var end = DateTimeOffset.UtcNow.AddDays(settings.CertificateDays);
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=SVM acceptance CA " + id.ToString("N"), rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var root = rootRequest.CreateSelfSigned(start, end);
        await WriteAsync(output, "private-certificates/root.pem", root.ExportCertificatePem(), token);
        await WriteAsync(output, "private-certificates/root.key", rootKey.ExportPkcs8PrivateKeyPem(), token);
        var leaves = new Dictionary<string, (string Sha256, string Sha1, string Spki)>();
        async Task Leaf(string name, bool server, params string[] hosts)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            if (hosts.Length > 0)
            {
                var san = new SubjectAlternativeNameBuilder();
                foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(host);
                request.CertificateExtensions.Add(san.Build());
            }
            var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7f; serial[0] |= 1;
            using var issued = request.Create(root, start, end, serial);
            using var certificate = issued.CopyWithPrivateKey(key);
            await WriteAsync(output, "private-certificates/" + name + ".pem", certificate.ExportCertificatePem(), token);
            await WriteAsync(output, "private-certificates/" + name + ".key", key.ExportPkcs8PrivateKeyPem(), token);
            await WriteBytesAsync(output, "private-certificates/" + name + ".pfx", certificate.Export(X509ContentType.Pkcs12, password), token);
            var sha1 = certificate.GetCertHashString(HashAlgorithmName.SHA1);
            leaves.Add(name, (certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(), string.Join(':', Enumerable.Range(0, 20).Select(n => sha1.Substring(n * 2, 2))),
                Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))));
        }
        await Leaf("protection", false);
        foreach (var node in new[] { "node-a", "node-b" })
        {
            await Leaf("server-" + node, true, settings.PublicHost, "127.0.0.1", "gateway", "api-" + node[^1]);
            foreach (var role in new[] { "peer", "gateway", "collector" }) await Leaf(role + "-" + node, false);
        }
        await Leaf("rabbitmq", true, "rabbitmq");
        string Connection(string role) => $"Host=postgres;Port=5432;Database=svm_site;Username=svm_site_{role};Password={databaseSecrets[role]};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=20;Include Error Detail=false;Log Parameters=false;";
        var persistence = new { writerConnectionString = Connection("app"), readerConnectionString = Connection("read") };
        object Personnel(string path) => new { applicationName = "svm/" + id, certificatePath = path + "/certs/protection.pfx", certificatePassword = password,
            policy = new { sessionHours = 8, minimumPasswordLength = 15, maximumPasswordLength = 128, hashIterations = 600000, accountFailures = 5, accountWindowSeconds = 300, addressFailures = 30, addressWindowSeconds = 60 } };
        var peers = new[] { "node-a", "node-b" }.SelectMany(node => new[] { "Peer", "Gateway", "Collector" }.Select(role => new {
            certificateSha256 = leaves[role.ToLowerInvariant() + "-" + node].Sha256, nodeId = node, subjectId = Guid.NewGuid(), role })).ToArray();
        var nodes = new[] { "node-a", "node-b" }.Select(node => new { nodeId = node, internalBaseUri = "https://api-" + node[^1] + ":7444/", serverCertificateSha256 = leaves["server-" + node].Sha256 }).ToArray();
        async Task CopyCertificate(string name, string destination) => await CopyAsync(Path.Combine(output, "private-certificates", name), output, destination, token);
        foreach (var node in new[] { "node-a", "node-b" })
        {
            var directory = "runtime/" + node;
            foreach (var name in new[] { "root.pem", "protection.pfx", "server-" + node + ".pfx", "peer-" + node + ".pfx", "collector-" + node + ".pfx" })
                await CopyCertificate(name, directory + "/certs/" + name);
            await JsonAsync(output, directory + "/persistence.json", persistence, token);
            await JsonAsync(output, directory + "/personnel.json", Personnel("/svm/config"), token);
            await JsonAsync(output, directory + "/site.json", new { siteId = id, settings.SiteName, siteTimeZone = settings.TimeZone }, token);
            await JsonAsync(output, directory + "/instances.json", settings.Instances, token);
            await JsonAsync(output, directory + "/host.json", new { publicCertificatePath = "/svm/config/certs/server-" + node + ".pfx", publicCertificatePassword = password, knownProxies = new[] { settings.GatewayAddress }, healthPort = 8081 }, token);
            await JsonAsync(output, directory + "/plc.json", new { uploadCredentialMaxLifetimeSeconds = settings.PlcUploadCredentialMaxLifetimeSeconds }, token);
            await JsonAsync(output, directory + "/messaging.json", new { host = "rabbitmq", port = 5671, virtualHost = brokerVhost, username = "svm_service", password = brokerSecret, siteId = id, useTls = true, tlsServerName = "rabbitmq" }, token);
            await JsonAsync(output, directory + "/tasks.json", settings.Task, token);
            await JsonAsync(output, directory + "/package.json", new { nodeId = node, rootPath = "/svm/packages", serviceSubjectId = Guid.NewGuid(), internalListenAddress = "0.0.0.0", internalListenPort = 7444,
                serverCertificatePath = "/svm/config/certs/server-" + node + ".pfx", serverCertificatePassword = password,
                clientCertificatePath = "/svm/config/certs/peer-" + node + ".pfx", clientCertificatePassword = password, trustCertificatePath = "/svm/config/certs/root.pem",
                collectorCertificatePath = "/svm/config/certs/collector-" + node + ".pfx", collectorCertificatePassword = password, downloadLogDirectory = "/svm/logs",
                limits = settings.PackageLimits, execution = settings.PackageExecution, nodes, peers }, token);
            PrivateDirectory(Path.Combine(output, "packages", node)); PrivateDirectory(Path.Combine(output, "logs", node));
        }
        await JsonAsync(output, "init/migration.json", new { connectionString = Connection("migrate"), writerRole = "svm_site_app", readerRole = "svm_site_read", enableInboxWrites = true }, token);
        await JsonAsync(output, "init/personnel.json", Personnel("/svm/init"), token);
        await CopyCertificate("protection.pfx", "init/certs/protection.pfx");
        await JsonAsync(output, "init/seed.json", new { writerConnectionString = persistence.writerConnectionString, personnelConfigurationFile = "/svm/init/personnel.json", settings.EmployeeNo, displayName = settings.AdministratorName, temporaryPassword = initialPassword }, token);
        PrivateDirectory(Path.Combine(output, "postgres/data"));
        await WriteAsync(output, "postgres/bootstrap-password", databaseSecrets["bootstrap"], token);
        // All role identifiers and generated password alphabet are fixed here, never SQL from input.
        var sql = string.Join('\n', new[] { "migrate", "app", "read" }.Select(role => $"CREATE ROLE svm_site_{role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD '{databaseSecrets[role]}';")) +
            "\nALTER ROLE svm_site_read SET default_transaction_read_only=on;\nCREATE DATABASE svm_site OWNER svm_site_migrate;\nREVOKE ALL ON DATABASE svm_site FROM PUBLIC;\nGRANT CONNECT ON DATABASE svm_site TO svm_site_migrate,svm_site_app,svm_site_read;\n\\connect svm_site\nREVOKE CREATE ON SCHEMA public FROM PUBLIC;\n";
        await WriteAsync(output, "postgres/init.sql", sql, token);
        PrivateDirectory(Path.Combine(output, "rabbitmq/data"));
        foreach (var name in new[] { "root.pem", "rabbitmq.pem", "rabbitmq.key" }) await CopyCertificate(name, "rabbitmq/config/certs/" + name);
        var salt = RandomNumberGenerator.GetBytes(4); var brokerHash = Convert.ToBase64String(salt.Concat(SHA256.HashData(salt.Concat(Encoding.UTF8.GetBytes(brokerSecret)).ToArray())).ToArray());
        await JsonAsync(output, "rabbitmq/config/definitions.json", new { users = new[] { new { name = "svm_service", password_hash = brokerHash, hashing_algorithm = "rabbit_password_hashing_sha256", tags = "" } },
            vhosts = new[] { new { name = brokerVhost, metadata = new { default_queue_type = "quorum" } } }, permissions = new[] { new { user = "svm_service", vhost = brokerVhost, configure = ".*", write = ".*", read = ".*" } } }, token);
        await WriteAsync(output, "rabbitmq/config/rabbitmq.conf", "load_definitions = /etc/rabbitmq/definitions.json\nlisteners.tcp = none\nlisteners.ssl.default = 5671\nssl_options.cacertfile = /etc/rabbitmq/certs/root.pem\nssl_options.certfile = /etc/rabbitmq/certs/rabbitmq.pem\nssl_options.keyfile = /etc/rabbitmq/certs/rabbitmq.key\nssl_options.verify = verify_none\nssl_options.fail_if_no_peer_cert = false\n", token);
        await CopyCertificate("root.pem", "gateway/certs/root.pem");
        foreach (var node in new[] { "node-a", "node-b" })
            foreach (var name in new[] { "server-" + node + ".pem", "server-" + node + ".key", "gateway-" + node + ".pem", "gateway-" + node + ".key" }) await CopyCertificate(name, "gateway/certs/" + name);
        var nginx = await File.ReadAllTextAsync(Path.Combine(bundle, "templates/nginx.conf"), token);
        foreach (var node in new[] { "node-a", "node-b" }) nginx = nginx.Replace("@@GENERATION_" + node + "@@", Guid.NewGuid().ToString("D"), StringComparison.Ordinal)
            .Replace("@@FINGERPRINT_" + node + "@@", leaves["gateway-" + node].Sha1.Replace(":", "").ToLowerInvariant(), StringComparison.Ordinal);
        nginx = nginx.Replace("@@MAX_BYTES@@", settings.PackageLimits.MaxPackageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@@IDLE_SECONDS@@", settings.PackageLimits.UploadIdleSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@@AUTH_SECONDS@@", settings.PackageExecution.DownloadAuthorizationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (nginx.Contains("@@", StringComparison.Ordinal)) throw new IOException("Incomplete gateway template.");
        await WriteAsync(output, "gateway/nginx.conf", nginx, token);
        foreach (var entry in manifest.Files.Where(x => x.Path.StartsWith("release/", StringComparison.Ordinal))) await CopyAsync(CandidateBundle.Within(bundle, entry.Path), output, "image/" + entry.Path, token);
        await CopyCertificate("root.pem", "image/ca.crt");
        await CopyAsync(Path.Combine(bundle, "templates/Dockerfile"), output, "image/Dockerfile", token);
        PrivateDirectory(Path.Combine(output, "recovery"));
        foreach (var file in new[] { "compose.yaml", "compose.preview.yaml", "compose.recovery.yaml", "compose.review.yaml", "svm.sh", "operations.sh" })
        {
            await CopyAsync(Path.Combine(bundle, "templates", file), output, file, token);
            if (!OperatingSystem.IsWindows() && file.EndsWith(".sh", StringComparison.Ordinal)) File.SetUnixFileMode(Path.Combine(output, file), DirectoryModePrivate);
        }
        var environment = new Dictionary<string, string> {
            ["SVM_PROJECT"] = project, ["SVM_CODE_COMMIT"] = manifest.Commit, ["SVM_RUNTIME_IMAGE"] = "svm/runtime:" + project,
            ["SVM_UID"] = settings.ServiceUid.ToString(System.Globalization.CultureInfo.InvariantCulture), ["SVM_GID"] = settings.ServiceGid.ToString(System.Globalization.CultureInfo.InvariantCulture), ["SVM_MODE"] = "full", ["SVM_PLATFORM"] = manifest.Platform,
            ["SVM_PUBLIC_BIND"] = settings.PublicBind, ["SVM_HTTPS_PORT"] = settings.HttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture), ["SVM_SECONDARY_HTTPS_PORT"] = settings.SecondaryHttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SVM_NETWORK_SUBNET"] = settings.NetworkSubnet, ["SVM_GATEWAY_ADDRESS"] = settings.GatewayAddress,
            ["SVM_PACKAGE_CONFIG_FILE"] = "/svm/config/package.json", ["SVM_MESSAGING_CONFIG_FILE"] = "/svm/config/messaging.json", ["SVM_TASK_CONFIG_FILE"] = "/svm/config/tasks.json", ["SVM_PLC_CONFIG_FILE"] = "/svm/config/plc.json", ["SVM_GITLAB_CONFIG_FILE"] = "" };
        foreach (var image in manifest.Images.Where(i => i.Role != "ASPNET")) environment.Add("SVM_" + image.Role + "_IMAGE", image.Tag);
        await WriteAsync(output, "deployment.env", string.Join('\n', environment.Select(x => x.Key + "=" + x.Value)) + "\n", token);
        await JsonAsync(output, "operator.json", new { url = "https://" + settings.PublicHost + ":" + settings.HttpsPort, secondaryUrl = "https://" + settings.PublicHost + ":" + settings.SecondaryHttpsPort,
            settings.EmployeeNo, initialPassword, caFile = "private-certificates/root.pem", browserSpkiPins = new[] { leaves["server-node-a"].Spki, leaves["server-node-b"].Spki } }, token);
        await JsonAsync(output, "deployment.json", new { format = 1, demo = false, purpose = "acceptance", deploymentId = id, project, platform = manifest.Platform, mode = "full", releaseCommit = manifest.Commit, releaseDirty = false,
            manifest.CandidateId, resourceDirectories = new[] { "postgres", "rabbitmq", "packages", "logs" }, certificateExpiresWithinDays = settings.CertificateDays, certificateMode = settings.CertificateMode,
            settings.ServiceUid, settings.ServiceGid, gitLabConfigured = false }, token);
        await WriteAsync(output, ".svm-deployment", project + "\n", token);
        File.Delete(Path.Combine(output, ".initializing"));
        return new(project, id, manifest.CandidateId);
    }
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void PrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, DirectoryModePrivate);
    }
    private static Task JsonAsync<T>(string output, string name, T value, CancellationToken token) => WriteAsync(output, name, JsonSerializer.Serialize(value, CandidateBundle.Json) + "\n", token);
    private static Task WriteAsync(string output, string name, string text, CancellationToken token) => WriteBytesAsync(output, name, Encoding.UTF8.GetBytes(text), token);
    private static async Task WriteBytesAsync(string output, string name, byte[] bytes, CancellationToken token)
    {
        var path = CandidateBundle.Within(output, name); PrivateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModePrivate;
        await using var file = new FileStream(path, options); await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true);
    }
    private static async Task CopyAsync(string source, string output, string name, CancellationToken token)
    {
        var path = CandidateBundle.Within(output, name); PrivateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModePrivate;
        await using var input = File.OpenRead(source); await using var file = new FileStream(path, options);
        await input.CopyToAsync(file, token); await file.FlushAsync(token); file.Flush(true);
    }
}
