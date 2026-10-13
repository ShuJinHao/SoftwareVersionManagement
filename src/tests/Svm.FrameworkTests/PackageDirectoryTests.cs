using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PackageDirectoryTests
{
    [Fact]
    public void StartupCreatesPrivateRootAndRepeatedValidationPreservesExistingFilesAndModes()
    {
        using var f = new Directories();
        var path = Path.Combine(f.Path, "private.json");
        File.WriteAllText(path, JsonSerializer.Serialize(f.Options));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var options = PackageFileOptions.Load(path)!;
        Assert.Empty(Directory.EnumerateFileSystemEntries(options.RootPath));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(options.RootPath));
        var package = System.IO.Path.Combine(options.RootPath, "existing-package.bin"); File.WriteAllBytes(package, [1, 2, 3]);
        new ServiceCollection().AddSvmPackageFiles(options); options.Validate();
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(package));
        Assert.Equal(new[] { package }, Directory.GetFiles(options.RootPath));
    }

    [Fact]
    public void ReadOnlyDownloadLogsAreAcceptedWithoutChangingPermissionsOrContents()
    {
        using var f = new Directories(); var logs = f.Options.DownloadLogDirectory;
        var segment = System.IO.Path.Combine(logs, Guid.NewGuid().ToString("D") + ".jsonl");
        File.WriteAllText(segment, "owned download log\n");
        if (!OperatingSystem.IsWindows())
        { f.Mode(segment, UnixFileMode.UserRead); f.Mode(logs, UnixFileMode.UserRead | UnixFileMode.UserExecute); }
        f.Options.Validate(); f.Options.Validate();
        Assert.Equal("owned download log\n", File.ReadAllText(segment));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Options.RootPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute, File.GetUnixFileMode(logs));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(segment));
        }
    }

    [Theory]
    [InlineData("root-link")]
    [InlineData("root-parent-link")]
    [InlineData("logs-link")]
    [InlineData("logs-parent-link")]
    [InlineData("dangling-root-link")]
    [InlineData("root-file")]
    [InlineData("logs-file")]
    [InlineData("root-parent-file")]
    [InlineData("missing-logs")]
    public void UnsafeOrMissingDirectoriesFailDuringStartupWithoutLeakingPaths(string fault)
    {
        using var f = new Directories(); var options = f.Options;
        var link = System.IO.Path.Combine(f.Path, "link"); var target = System.IO.Path.Combine(f.Path, "target");
        if (fault.Contains("link", StringComparison.Ordinal))
        {
            if (fault != "dangling-root-link") Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);
        }
        options = fault switch
        {
            "root-link" or "dangling-root-link" => options with { RootPath = link },
            "root-parent-link" => options with { RootPath = System.IO.Path.Combine(link, "files") },
            "logs-link" => options with { DownloadLogDirectory = link },
            "logs-parent-link" => options with { DownloadLogDirectory = System.IO.Path.Combine(link, "logs") },
            _ => options
        };
        if (fault == "root-file") File.WriteAllText(options.RootPath, "existing file");
        if (fault == "logs-file") { Directory.Delete(options.DownloadLogDirectory); File.WriteAllText(options.DownloadLogDirectory, "existing file"); }
        if (fault == "root-parent-file") { File.WriteAllText(target, "existing file"); options = options with { RootPath = System.IO.Path.Combine(target, "files") }; }
        if (fault == "missing-logs") Directory.Delete(options.DownloadLogDirectory);
        f.Rejected(options);
        Assert.Empty(Directory.GetFiles(f.Path, ".svm-access-*"));
        if (Directory.Exists(f.Options.RootPath)) Assert.Empty(Directory.GetFiles(f.Options.RootPath, ".svm-access-*"));
    }

    [Theory]
    [InlineData("root-read-only")]
    [InlineData("root-no-read")]
    [InlineData("logs-no-read")]
    [InlineData("logs-no-search")]
    [InlineData("log-file-no-read")]
    public void MissingActualAccessFailsDuringStartupAndLeavesNoProbe(string fault)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This permission fixture targets the supported Linux/macOS verification hosts.");
        using var f = new Directories(); Directory.CreateDirectory(f.Options.RootPath);
        if (fault == "root-read-only") f.Mode(f.Options.RootPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (fault == "root-no-read") f.Mode(f.Options.RootPath, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (fault == "logs-no-read") f.Mode(f.Options.DownloadLogDirectory, UnixFileMode.UserExecute);
        if (fault == "logs-no-search") f.Mode(f.Options.DownloadLogDirectory, UnixFileMode.UserRead);
        if (fault == "log-file-no-read")
        { var segment = System.IO.Path.Combine(f.Options.DownloadLogDirectory, "private.jsonl"); File.WriteAllText(segment, "owned log"); f.Mode(segment, 0); }
        f.Rejected(f.Options); f.RestoreModes();
        Assert.Empty(Directory.GetFiles(f.Path, ".svm-access-*", SearchOption.AllDirectories));
    }

    private sealed class Directories : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(OutboxFixture.Root, ".cache", "package-directories-" + Guid.NewGuid().ToString("N"));
        internal PackageFileOptions Options { get; }
        private readonly List<(string Path, UnixFileMode Mode)> _modes = [];
        internal Directories()
        {
            Directory.CreateDirectory(Path);
            var logs = System.IO.Path.Combine(Path, "logs"); Directory.CreateDirectory(logs);
            var server = Certificate("server"); var peer = Certificate("peer"); var collector = Certificate("collector");
            var trust = System.IO.Path.Combine(Path, "trust.cer");
            using (var certificate = PackageFileOptions.LoadCertificate(server.Path, server.Password)) File.WriteAllBytes(trust, certificate.Export(X509ContentType.Cert));
            Options = new("node-a", System.IO.Path.Combine(Path, "files"), Guid.NewGuid(), "127.0.0.1", 20443,
                server.Path, server.Password, peer.Path, peer.Password, trust, collector.Path, collector.Password, logs,
                PackageFixture.Limits, PackageFixture.Execution,
                [new("node-a", "https://node-a.invalid/", server.Fingerprint), new("node-b", "https://node-b.invalid/", server.Fingerprint)],
                [new(peer.Fingerprint, "node-a", Guid.NewGuid(), "Peer"), new(collector.Fingerprint, "node-a", Guid.NewGuid(), "Collector"),
                 new(server.Fingerprint, "node-a", Guid.NewGuid(), "Gateway"), new(new string('a', 64), "node-b", Guid.NewGuid(), "Peer")]);
        }
        private (string Path, string Password, string Fingerprint) Certificate(string name)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=owned-directory-fixture-" + name, key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var path = System.IO.Path.Combine(Path, name + ".pfx"); var password = Guid.NewGuid().ToString("N");
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return (path, password, certificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
        internal void Rejected(PackageFileOptions options)
        {
            var error = Assert.Throws<RequestRejectedException>(options.Validate);
            Assert.Equal(RequestFailure.ConfigurationInvalid, error.Failure);
            PersistenceDatabase.AssertRedacted(error.ToString(), Path);
            PersistenceDatabase.AssertRedacted(error.ToString(), Options.ServerCertificatePassword!);
        }
        internal void Mode(string path, UnixFileMode mode)
        { if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); _modes.Add((path, File.GetUnixFileMode(path))); File.SetUnixFileMode(path, mode); }
        internal void RestoreModes()
        { if (!OperatingSystem.IsWindows()) foreach (var (path, mode) in _modes.AsEnumerable().Reverse()) File.SetUnixFileMode(path, mode); _modes.Clear(); }
        public void Dispose() { RestoreModes(); Directory.Delete(Path, true); }
    }
}
