using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Svm.ServiceDefaults;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class CandidateDeploymentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public async Task ExplicitSettingsRejectMissingUnknownOrInvalidInputsBeforeCreatingData()
    {
        using var fixture = new Fixture();
        foreach (var change in new Action<JsonObject>[] {
            x => x.Remove("plcUploadCredentialMaxLifetimeSeconds"), x => x["apiToken"] = "must-never-be-accepted",
            x => x["publicHost"] = "https://example.invalid/path", x => x["timeZone"] = "Not/AZone",
            x => x["certificateMode"] = "production", x => x["serviceUid"] = 0,
            x => x["dataDirectory"] = "/srv/a/../b", x => x["dataDirectory"] = "/srv/a\nanything",
            x => x["gatewayAddress"] = "172.31.248.1", x => x["gatewayAddress"] = "192.0.2.1",
            x => x["secondaryHttpsPort"] = 9443, x => x["task"] = null,
            x => x["packageExecution"]!["downloadAuthorizationSeconds"] = 30 })
        {
            var input = Settings(); change(input); await File.WriteAllTextAsync(fixture.Settings, input.ToJsonString());
            await Assert.ThrowsAnyAsync<Exception>(() => AcceptanceDeployment.InitializeAsync(fixture.Settings, fixture.Bundle, fixture.Output, default));
            Assert.False(Directory.Exists(fixture.Output));
        }
    }

    [Fact]
    public async Task CandidateRejectsChangedUnlistedOrWrongArchitectureMaterial()
    {
        using var fixture = new Fixture(); await fixture.BuildAsync();
        var manifest = await CandidateBundle.VerifyAsync(fixture.Bundle, default);
        Assert.Equal("linux/amd64", manifest.Platform);
        var image = Path.Combine(fixture.Bundle, "images.tar"); await File.WriteAllTextAsync(image, "altered");
        await Assert.ThrowsAnyAsync<Exception>(() => CandidateBundle.VerifyAsync(fixture.Bundle, default));
        await File.WriteAllTextAsync(image, "archive-fixture");
        var extra = Path.Combine(fixture.Bundle, "unexpected-secret.json"); await File.WriteAllTextAsync(extra, "do not ship");
        await Assert.ThrowsAnyAsync<Exception>(() => CandidateBundle.VerifyAsync(fixture.Bundle, default)); File.Delete(extra);
        await File.WriteAllTextAsync(Path.Combine(fixture.Bundle, "candidate.json"), JsonSerializer.Serialize(manifest with { Platform = "linux/arm64" }, Json));
        await Assert.ThrowsAnyAsync<Exception>(() => CandidateBundle.VerifyAsync(fixture.Bundle, default));
    }

    [Fact]
    public async Task CandidateRejectsDirtyReleaseAndTraversalEvenWithConsistentOuterHashes()
    {
        using var fixture = new Fixture(); await fixture.BuildAsync(dirty: true);
        await Assert.ThrowsAnyAsync<Exception>(() => CandidateBundle.VerifyAsync(fixture.Bundle, default));
        var path = Path.Combine(fixture.Bundle, "candidate.json"); var manifest = JsonSerializer.Deserialize<CandidateBundle.Manifest>(await File.ReadAllTextAsync(path), Json)!;
        var files = manifest.Files.ToArray(); files[0] = files[0] with { Path = "../outside" };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest with { Files = files }, Json));
        await Assert.ThrowsAnyAsync<Exception>(() => CandidateBundle.VerifyAsync(fixture.Bundle, default));
    }

    [Fact]
    public async Task InitializationCreatesPrivateStableIdentityEmptyLedgerAndExplicitFeatureConfiguration()
    {
        using var fixture = new Fixture(); await fixture.BuildAsync();
        var before = await fixture.Hashes(fixture.Bundle);
        var result = await AcceptanceDeployment.InitializeAsync(fixture.Settings, fixture.Bundle, fixture.Output, default);
        Assert.StartsWith("svm-site-", result.Project);
        Assert.False(File.Exists(Path.Combine(fixture.Output, ".initializing")));
        var descriptor = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Output, "deployment.json")))!;
        Assert.False(descriptor["demo"]!.GetValue<bool>()); Assert.Equal("acceptance", descriptor["purpose"]!.GetValue<string>());
        var first = await fixture.Hashes(fixture.Output);
        await Assert.ThrowsAnyAsync<Exception>(() => AcceptanceDeployment.InitializeAsync(fixture.Settings, fixture.Bundle, fixture.Output, default));
        Assert.Equal(first, await fixture.Hashes(fixture.Output)); Assert.Equal(before, await fixture.Hashes(fixture.Bundle));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Output, "postgres/data")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Output, "packages/node-a")));
        var env = await File.ReadAllTextAsync(Path.Combine(fixture.Output, "deployment.env"));
        Assert.Contains("SVM_PLC_CONFIG_FILE=/svm/config/plc.json", env); Assert.Contains("SVM_GITLAB_CONFIG_FILE=\n", env);
        Assert.DoesNotContain("secret", env, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(fixture.Output, "runtime/node-a/site.json")), await File.ReadAllTextAsync(Path.Combine(fixture.Output, "runtime/node-b/site.json")));
        var seed = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Output, "init/seed.json")))!;
        Assert.Equal("FIXTURE-ADMIN", seed["employeeNo"]!.GetValue<string>());
        Assert.True(seed["temporaryPassword"]!.GetValue<string>().Length >= 32);
        Assert.DoesNotContain("CREATE TABLE", await File.ReadAllTextAsync(Path.Combine(fixture.Output, "postgres/init.sql")), StringComparison.Ordinal);
        var package = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Output, "runtime/node-a/package.json")))!;
        using var cert = new X509Certificate2(Path.Combine(fixture.Output, "runtime/node-a/certs/server-node-a.pfx"), package["serverCertificatePassword"]!.GetValue<string>(), OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(cert.HasPrivateKey); Assert.Equal(package["nodes"]![0]!["serverCertificateSha256"]!.GetValue<string>(), cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant());
        using var root = new X509Certificate2(Path.Combine(fixture.Output, "runtime/node-a/certs/root.pem"));
        using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(root); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; chain.ChainPolicy.DisableCertificateDownloads = true;
        Assert.True(chain.Build(cert));
        var nginx = await File.ReadAllTextAsync(Path.Combine(fixture.Output, "gateway/nginx.conf")); Assert.DoesNotContain("@@", nginx);
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.EnumerateFiles(fixture.Output, "*", SearchOption.AllDirectories))
                Assert.Equal(0, (int)File.GetUnixFileMode(file) & 63);
    }

    [Fact]
    public async Task ExistingDirectoryAndSymbolicParentCannotBeInitialized()
    {
        using var fixture = new Fixture(); await fixture.BuildAsync(); Directory.CreateDirectory(fixture.Output);
        await File.WriteAllTextAsync(Path.Combine(fixture.Output, "owner.txt"), "keep");
        await Assert.ThrowsAnyAsync<Exception>(() => AcceptanceDeployment.InitializeAsync(fixture.Settings, fixture.Bundle, fixture.Output, default));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(fixture.Output, "owner.txt")));
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(fixture.Root, "linked"); Directory.CreateSymbolicLink(link, fixture.Output);
            await Assert.ThrowsAnyAsync<Exception>(() => AcceptanceDeployment.InitializeAsync(fixture.Settings, fixture.Bundle, Path.Combine(link, "new"), default));
            Assert.False(Directory.Exists(Path.Combine(fixture.Output, "new"))); Directory.Delete(link);
        }
    }

    private static JsonObject Settings() => JsonNode.Parse("""
        {"siteName":"虚构验收厂区","timeZone":"Asia/Shanghai","publicHost":"localhost","publicBind":"127.0.0.1","httpsPort":9443,"secondaryHttpsPort":9444,
         "dataDirectory":"/srv/svm/中文 spaced","serviceUid":10001,"serviceGid":10001,"networkSubnet":"172.31.248.0/28","gatewayAddress":"172.31.248.10",
         "employeeNo":"FIXTURE-ADMIN","administratorName":"虚构验收管理员","certificateMode":"acceptance-test","certificateDays":30,
         "instances":{"enrollmentMaxLifetimeSeconds":86400,"enrollmentMaxCount":100,"recoveryMaxLifetimeSeconds":1800},"plcUploadCredentialMaxLifetimeSeconds":86400,
         "packageLimits":{"maxPackageBytes":33554432,"uploadIdleSeconds":30},"packageExecution":{"leaseSeconds":60,"pollSeconds":5,"replicaCheckSeconds":60,"workBatchSize":20,"peerResponseSeconds":15,"downloadAuthorizationSeconds":20},
         "task":{"defaultStartLocalTime":"00:00","defaultLatestStartLocalTime":"23:59","batchSize":10,"failureLimit":2,"resultWaitSeconds":300,"selectionChunkSize":100,"leaseSeconds":60,"pollRetrySeconds":2,"maxSelectionMembers":1000,"snapshotTimeoutSeconds":10}}
        """)!.AsObject();

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "svm-candidate-" + Guid.NewGuid().ToString("N"));
        public string Bundle => Path.Combine(Root, "bundle");
        public string Output => Path.Combine(Root, "中文 private output");
        public string Settings => Path.Combine(Root, "settings.json");
        public Fixture() => Directory.CreateDirectory(Root);
        public async Task BuildAsync(bool dirty = false)
        {
            const string commit = "1111111111111111111111111111111111111111", candidate = "RC-20261106-1111111";
            Directory.CreateDirectory(Bundle); Directory.CreateDirectory(Path.Combine(Bundle, "templates")); Directory.CreateDirectory(Path.Combine(Bundle, "release"));
            await File.WriteAllTextAsync(Settings, CandidateDeploymentTests.Settings().ToJsonString());
            foreach (var name in new[] { "Dockerfile", "compose.yaml", "compose.preview.yaml", "compose.recovery.yaml", "compose.review.yaml", "svm.sh", "operations.sh" }) await File.WriteAllTextAsync(Path.Combine(Bundle, "templates", name), "fixture");
            await File.WriteAllTextAsync(Path.Combine(Bundle, "templates/nginx.conf"), "@@GENERATION_node-a@@ @@GENERATION_node-b@@ @@FINGERPRINT_node-a@@ @@FINGERPRINT_node-b@@ @@MAX_BYTES@@ @@IDLE_SECONDS@@ @@AUTH_SECONDS@@");
            await File.WriteAllTextAsync(Path.Combine(Bundle, "images.tar"), "archive-fixture"); await File.WriteAllTextAsync(Path.Combine(Bundle, "install.sh"), "fixture");
            var images = new[] { "ASPNET", "POSTGRES", "RABBITMQ", "NGINX" }.Select(role => new CandidateBundle.Image(role, "registry/image@sha256:" + new string('a', 64), "svm-candidate/" + candidate.ToLowerInvariant() + ":" + role.ToLowerInvariant(), "sha256:" + new string('b', 64), "linux/amd64")).ToArray();
            await File.WriteAllLinesAsync(Path.Combine(Bundle, "images.lock.tsv"), images.Select(i => $"{i.Role}\t{i.Reference}\t{i.Tag}\t{i.Id}\t{i.Platform}"));
            await File.WriteAllTextAsync(Path.Combine(Bundle, "release/release.json"), JsonSerializer.Serialize(new { commit, dirty, runtimeMinimum = "8.0.31", runtimeRollForward = "LatestPatch", files = Array.Empty<object>() }));
            var entries = new List<CandidateBundle.Entry>();
            foreach (var path in Directory.EnumerateFiles(Bundle, "*", SearchOption.AllDirectories))
                entries.Add(new(Path.GetRelativePath(Bundle, path).Replace('\\', '/'), new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant()));
            await File.WriteAllTextAsync(Path.Combine(Bundle, "candidate.json"), JsonSerializer.Serialize(new CandidateBundle.Manifest(1, candidate, commit, "linux/amd64", images, entries.ToArray()), Json));
        }
        public async Task<string> Hashes(string directory)
        {
            var text = new StringBuilder();
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                text.Append(Path.GetRelativePath(directory, path)).Append(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
            return text.ToString();
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
