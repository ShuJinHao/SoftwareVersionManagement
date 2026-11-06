using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace Svm.ServiceDefaults;

/// <summary>Cold, same-platform deployment backup. Docker orchestration must stop all writers first.</summary>
public static class DeploymentBackup
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly string[] Roots = ["runtime", "init", "gateway", "packages", "logs", "postgres", "rabbitmq", "private-certificates", "image",
        "deployment.env", "deployment.json", "deployment.offline.env", "images.lock.tsv", "operator.json", ".svm-demo", ".svm-deployment",
        "svm.sh", "operations.sh", "compose.yaml", "compose.preview.yaml", "compose.recovery.yaml", "compose.review.yaml", "recovery"];
    public sealed record Entry(string Path, long Size, string Sha256, int Mode);
    public sealed record Manifest(int Format, string Platform, string Project, string ReleaseCommit, DateTimeOffset CreatedAt,
        string[] Directories, Entry[] Files);
    public sealed record Options(string Source, string Destination, string Platform);
    public static async Task<Manifest> ExecuteAsync(string action, string configuration, CancellationToken token)
    {
        var options = JsonSerializer.Deserialize<Options>(await File.ReadAllTextAsync(configuration, token), Json) ?? throw new IOException();
        return action switch
        {
            "backup-seal" => await SealAsync(options, token),
            "backup-verify" => await VerifyAsync(options.Source, options.Platform, token),
            "backup-restore" => await RestoreAsync(options, token),
            _ => throw new IOException("Unknown backup operation.")
        };
    }
    internal static string SafeRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Absolute directory required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
            if (d.LinkTarget is not null) throw new IOException("Links are not backup directories.");
        return full;
    }
    private static string Within(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 4096 || name.Contains('\\') || name.Contains('\n') || name.Contains('\r') ||
            name.StartsWith('/') || name.Split('/').Any(s => s is "" or "." or "..")) throw new IOException("Invalid backup entry.");
        return Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
    }
    private static bool OwnPath(string name) => Roots.Any(root => name == root || name.StartsWith(root + '/', StringComparison.Ordinal));
    private static void Separate(string source, string destination)
    {
        if (source == destination || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Backup directories overlap.");
    }
    private static void EmptyDestination(string destination)
    {
        if (!Directory.Exists(destination)) Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("Destination must be new and empty.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static async Task PrivateJson<T>(string path, T value, CancellationToken token)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await JsonSerializer.SerializeAsync(stream, value, Json, token); await stream.FlushAsync(token); stream.Flush(true);
    }
    private static async Task<string> Digest(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Only regular files are allowed.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
    }
    private static async Task Copy(string source, string destination, CancellationToken token)
    {
        if (new FileInfo(source).LinkTarget is not null) throw new IOException("Links are not backup content.");
        PrivateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = new FileStream(destination, options);
        await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true);
    }
    public static async Task<Manifest> SealAsync(Options options, CancellationToken token)
    {
        var source = SafeRoot(options.Source); var destination = SafeRoot(options.Destination); Separate(source, destination);
        if (options.Platform is not ("linux/amd64" or "linux/arm64") || !File.Exists(Path.Combine(source, "recovery/quiesced.json")) ||
            File.Exists(Path.Combine(source, "postgres/data/postmaster.pid"))) throw new IOException("A stopped deployment is required.");
        using var deployment = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "deployment.json"), token));
        if (deployment.RootElement.GetProperty("platform").GetString() != options.Platform) throw new IOException("Platform mismatch.");
        var project = deployment.RootElement.GetProperty("project").GetString() ?? throw new IOException();
        var commit = deployment.RootElement.GetProperty("releaseCommit").GetString() ?? throw new IOException();
        EmptyDestination(destination); PrivateDirectory(Path.Combine(destination, "content"));
        var directories = new List<string>(); var files = new List<Entry>();
        async Task Walk(string name)
        {
            if (name == "recovery/operation.lock") return;
            token.ThrowIfCancellationRequested(); if (files.Count + directories.Count >= 250000) throw new IOException("Backup entry limit exceeded.");
            var path = Within(source, name);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Links are not backup content.");
            if (Directory.Exists(path))
            {
                directories.Add(name); PrivateDirectory(Within(Path.Combine(destination, "content"), name));
                foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal)) await Walk(name + '/' + Path.GetFileName(child));
            }
            else
            {
                var info = new FileInfo(path); var size = info.Length; var modified = info.LastWriteTimeUtc;
                var target = Within(Path.Combine(destination, "content"), name); await Copy(path, target, token);
                var hash = await Digest(target, token); info.Refresh();
                if (info.Length != size || info.LastWriteTimeUtc != modified || hash != await Digest(path, token)) throw new IOException("Source changed during backup.");
                var mode = OperatingSystem.IsWindows() ? 384 : (int)File.GetUnixFileMode(path) & 511;
                files.Add(new(name, size, hash, mode));
            }
        }
        foreach (var name in Roots) if (Path.Exists(Path.Combine(source, name))) await Walk(name);
        foreach (var required in new[] { "deployment.env", "deployment.json", "svm.sh", "compose.yaml", "postgres/data/PG_VERSION", "image/release/release.json" })
            if (!files.Any(x => x.Path == required)) throw new IOException("Backup is missing required material.");
        var manifest = new Manifest(1, options.Platform, project, commit, DateTimeOffset.UtcNow, directories.ToArray(), files.ToArray());
        await PrivateJson(Path.Combine(destination, "backup.json"), manifest, token);
        await VerifyAsync(destination, options.Platform, token);
        return manifest;
    }
    public static async Task<Manifest> VerifyAsync(string source, string platform, CancellationToken token)
    {
        source = SafeRoot(source); var manifestPath = Path.Combine(source, "backup.json");
        if (new FileInfo(manifestPath).Length > 64 * 1024 * 1024 || new FileInfo(manifestPath).LinkTarget is not null) throw new IOException("Invalid manifest.");
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, token), Json) ?? throw new IOException();
        if (manifest.Format != 1 || manifest.Platform != platform || platform is not ("linux/amd64" or "linux/arm64") ||
            manifest.Files is null || manifest.Directories is null || manifest.Files.Length is 0 or > 250000 || manifest.Directories.Length > 250000 ||
            !System.Text.RegularExpressions.Regex.IsMatch(manifest.Project ?? "", "^svm-(demo|site|restore)-[a-f0-9]{12}$") ||
            manifest.ReleaseCommit is not { Length: 40 } || !manifest.ReleaseCommit.All(char.IsAsciiHexDigit)) throw new IOException("Incompatible backup.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var content = SafeRoot(Path.Combine(source, "content"));
        foreach (var name in manifest.Directories)
        {
            if (!OwnPath(name) || !names.Add(name) || !Directory.Exists(Within(content, name))) throw new IOException("Invalid directory inventory.");
            SafeRoot(Within(content, name));
        }
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!OwnPath(file.Path) || !names.Add(file.Path) || file.Size < 0 || file.Mode is < 0 or > 511 ||
                file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid file inventory.");
            var path = Within(content, file.Path); SafeRoot(Path.GetDirectoryName(path)!);
            if (new FileInfo(path).Length != file.Size || await Digest(path, token) != file.Sha256) throw new IOException("Backup integrity failed.");
        }
        // Reject unlisted files and linked directories, not just missing expected entries.
        var count = 0;
        void Inventory(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 500000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    !names.Contains(Path.GetRelativePath(content, path).Replace('\\', '/'))) throw new IOException("Unexpected backup content.");
                if (Directory.Exists(path)) Inventory(path);
            }
        }
        Inventory(content);
        foreach (var required in new[] { "deployment.env", "deployment.json", "svm.sh", "compose.yaml", "postgres/data/PG_VERSION", "image/release/release.json" })
            if (!manifest.Files.Any(x => x.Path == required)) throw new IOException("Backup is missing required material.");
        if ((await File.ReadAllTextAsync(Within(content, "postgres/data/PG_VERSION"), token)).Trim() != "17") throw new IOException("Database version mismatch.");
        using var deployment = JsonDocument.Parse(await File.ReadAllTextAsync(Within(content, "deployment.json"), token));
        if (deployment.RootElement.GetProperty("project").GetString() != manifest.Project ||
            deployment.RootElement.GetProperty("releaseCommit").GetString() != manifest.ReleaseCommit ||
            deployment.RootElement.GetProperty("platform").GetString() != platform) throw new IOException("Deployment identity mismatch.");
        return manifest;
    }
    public static async Task<Manifest> RestoreAsync(Options options, CancellationToken token)
    {
        var source = SafeRoot(options.Source); var destination = SafeRoot(options.Destination); Separate(source, destination);
        var manifest = await VerifyAsync(source, options.Platform, token);
        EmptyDestination(destination); PrivateDirectory(Path.Combine(destination, "recovery"));
        await PrivateJson(Path.Combine(destination, "recovery/locked.json"), new { format = 1, sourceProject = manifest.Project,
            manifest.ReleaseCommit, backupAt = manifest.CreatedAt, restoredAt = DateTimeOffset.UtcNow, state = "Isolated",
            required = "Reconcile post-backup site actions before reopening; no automatic work execution." }, token);
        foreach (var directory in manifest.Directories.Where(x => x != "recovery" && !x.StartsWith("recovery/", StringComparison.Ordinal))) PrivateDirectory(Within(destination, directory));
        foreach (var file in manifest.Files)
        {
            // Never restore an old approval or bypass the freshly installed recovery fence.
            if (file.Path.StartsWith("recovery/", StringComparison.Ordinal)) continue;
            var target = Within(destination, file.Path); await Copy(Within(Path.Combine(source, "content"), file.Path), target, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, (UnixFileMode)file.Mode);
        }
        var project = "svm-restore-" + Guid.NewGuid().ToString("N")[..12];
        var environment = Path.Combine(destination, "deployment.env");
        var lines = await File.ReadAllLinesAsync(environment, token);
        await File.WriteAllLinesAsync(environment, lines.Select(line => line.StartsWith("SVM_PROJECT=", StringComparison.Ordinal) ? "SVM_PROJECT=" + project :
            line.StartsWith("SVM_RUNTIME_IMAGE=", StringComparison.Ordinal) ? "SVM_RUNTIME_IMAGE=svm/runtime:" + project : line), token);
        // An offline image lock belongs to the original deployment. Restore must rebuild/import
        // its exact recorded artifact explicitly, never silently retag an unrelated cached image.
        File.Delete(Path.Combine(destination, "deployment.offline.env")); File.Delete(Path.Combine(destination, "images.lock.tsv"));
        var deploymentPath = Path.Combine(destination, "deployment.json");
        var deployment = JsonNode.Parse(await File.ReadAllTextAsync(deploymentPath, token)) ?? throw new IOException();
        deployment["project"] = project; deployment["restoredFrom"] = manifest.Project;
        await File.WriteAllTextAsync(deploymentPath, deployment.ToJsonString(Json), token);
        var marker = File.Exists(Path.Combine(destination, ".svm-demo")) ? ".svm-demo" : ".svm-deployment";
        await File.WriteAllTextAsync(Path.Combine(destination, marker), project + "\n", token);
        // Old log segments, cursors and stop evidence remain history. New downloads
        // must use new process generations after restoring into another deployment.
        var gateway = Path.Combine(destination, "gateway/nginx.conf");
        if (File.Exists(gateway))
        {
            var text = await File.ReadAllTextAsync(gateway, token);
            var generations = System.Text.RegularExpressions.Regex.Matches(text, @"set \$svm_generation ([0-9a-f-]{36});")
                .Select(x => x.Groups[1].Value).Distinct().ToArray();
            if (generations.Length != 2 || generations.Any(x => !Guid.TryParseExact(x, "D", out _))) throw new IOException("Gateway generation inventory mismatch.");
            foreach (var generation in generations) text = text.Replace(generation, Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
            await File.WriteAllTextAsync(gateway, text, token);
        }
        await PrivateJson(Path.Combine(destination, "recovery/backup-reference.json"), manifest, token);
        return manifest;
    }
}
