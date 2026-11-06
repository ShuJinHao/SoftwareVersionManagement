using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Svm.Services.Contracts.Framework;

namespace Svm.ServiceDefaults;

public static class RecoveryApproval
{
    public sealed record Evidence(string Operator, string Reason, string EvidenceReference, bool SourceStopped,
        bool PostBackupChangesReconciled, bool PendingWorkReviewed, bool IdentityMaterialReviewed);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static async Task ExecuteAsync(string action, string root, CancellationToken token)
    {
        root = DeploymentBackup.SafeRoot(root);
        var directory = Path.Combine(root, "recovery"); var lockFile = Path.Combine(directory, "locked.json");
        if (new DirectoryInfo(directory).LinkTarget is not null || new FileInfo(lockFile).LinkTarget is not null || new FileInfo(lockFile).Length > 16384) throw new IOException();
        var state = JsonNode.Parse(await File.ReadAllTextAsync(lockFile, token)) ?? throw new IOException();
        if (action == "recovery-review")
        {
            state["state"] = "Review";
            await File.WriteAllTextAsync(lockFile, state.ToJsonString(Json), token); return;
        }
        if (action != "recovery-open" || state["state"]?.GetValue<string>() != "Review") throw new IOException();
        string Input(string name, long maximum)
        {
            var file = new FileInfo(Path.Combine(directory, name));
            if (!file.Exists || file.LinkTarget is not null || file.Length > maximum) throw new IOException();
            return file.FullName;
        }
        var evidence = JsonSerializer.Deserialize<Evidence>(await File.ReadAllTextAsync(Input("review-input.json", 16384), token), Json) ?? throw new IOException();
        if (string.IsNullOrWhiteSpace(evidence.Operator) || evidence.Operator.Length > 128 || string.IsNullOrWhiteSpace(evidence.Reason) || evidence.Reason.Length > 2000 ||
            string.IsNullOrWhiteSpace(evidence.EvidenceReference) || evidence.EvidenceReference.Length > 512 || !evidence.SourceStopped ||
            !evidence.PostBackupChangesReconciled || !evidence.PendingWorkReviewed || !evidence.IdentityMaterialReviewed) throw new IOException();
        var reportBytes = await File.ReadAllBytesAsync(Input("pre-open-report.json", 1024 * 1024), token);
        var report = JsonSerializer.Deserialize<OperationsSnapshot>(reportBytes, Json) ?? throw new IOException();
        if (report.Status != "Available" || report.ObservedAt > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - report.ObservedAt > TimeSpan.FromMinutes(2)) throw new IOException();
        foreach (var name in new[] { "recovery.unpausedDeployments", "recovery.validStartPermits" })
        {
            var metric = report.Metrics.Single(x => x.Name == name);
            if (metric.Status != "Available" || metric.Count != 0 || metric.Capped) throw new IOException();
        }
        var manifest = JsonSerializer.Deserialize<DeploymentBackup.Manifest>(await File.ReadAllTextAsync(Input("backup-reference.json", 64 * 1024 * 1024), token), Json) ?? throw new IOException();
        foreach (var entry in manifest.Files.Where(x => x.Path.StartsWith("packages/", StringComparison.Ordinal)))
        {
            if (entry.Path.Contains("..", StringComparison.Ordinal) || entry.Path.Contains('\\')) throw new IOException();
            var file = new FileInfo(Path.Combine(root, entry.Path));
            DeploymentBackup.SafeRoot(file.DirectoryName!);
            if (file.LinkTarget is not null || file.Length != entry.Size) throw new IOException();
            await using var stream = file.OpenRead();
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant() != entry.Sha256) throw new IOException();
        }
        var inventory = JsonSerializer.Deserialize<RecoveryPackages>(await File.ReadAllTextAsync(Input("packages-report.json", 32 * 1024 * 1024), token), Json) ?? throw new IOException();
        if (inventory.Packages.Count > 100000 || inventory.ObservedAt > DateTimeOffset.UtcNow || inventory.ObservedAt < report.ObservedAt.AddMinutes(-2)) throw new IOException();
        foreach (var package in inventory.Packages)
        {
            if (package.Id == Guid.Empty || package.Size < 1 || package.Sha256 is not { Length: 64 } || !package.Sha256.All(char.IsAsciiHexDigit)) throw new IOException();
            foreach (var node in new[] { "node-a", "node-b" })
            {
                var file = new FileInfo(Path.Combine(root, "packages", node, "replicas", package.Id.ToString("D") + ".bin"));
                DeploymentBackup.SafeRoot(file.DirectoryName!);
                if (!file.Exists || file.LinkTarget is not null || file.Length != package.Size) throw new IOException();
                await using var stream = file.OpenRead();
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            }
        }
        var approval = new { approvedAt = DateTimeOffset.UtcNow, evidence, state,
            reportSha256 = Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant() };
        var path = Path.Combine(directory, "approval-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(approval, Json), token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // Removal is the last step; a crash before it keeps all services fenced.
        File.Delete(lockFile);
    }
}
