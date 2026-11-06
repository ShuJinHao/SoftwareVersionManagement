using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Svm.ServiceDefaults;

/// <summary>Verifies public, immutable installation material before private initialization.</summary>
public static class CandidateBundle
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public sealed record Entry(string Path, long Size, string Sha256);
    public sealed record Image(string Role, string Reference, string Tag, string Id, string Platform);
    public sealed record Manifest(int Format, string CandidateId, string Commit, string Platform, Image[] Images, Entry[] Files);

    public static async Task<Manifest> VerifyAsync(string directory, CancellationToken token)
    {
        directory = DeploymentBackup.SafeRoot(directory);
        var path = Path.Combine(directory, "candidate.json");
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("Invalid candidate manifest.");
        var value = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, token), Json) ?? throw new IOException();
        if (value.Format != 1 || value.Platform != "linux/amd64" || value.Commit is null || value.CandidateId is null || !Regex.IsMatch(value.Commit, "^[a-f0-9]{40}$") ||
            !Regex.IsMatch(value.CandidateId, "^RC-[0-9]{8}-[a-f0-9]{7,40}$") ||
            !value.Commit.StartsWith(value.CandidateId[12..], StringComparison.Ordinal) || value.Images is not { Length: 4 } ||
            value.Files is not { Length: > 0 and <= 10000 }) throw new IOException("Incompatible candidate.");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in value.Images)
            if (!roles.Add(image.Role) || image.Role is not ("ASPNET" or "POSTGRES" or "RABBITMQ" or "NGINX") || image.Platform != value.Platform ||
                !Regex.IsMatch(image.Id ?? "", "^sha256:[a-f0-9]{64}$") || !Regex.IsMatch(image.Reference ?? "", "^[A-Za-z0-9./:_-]+@sha256:[a-f0-9]{64}$") ||
                image.Tag != "svm-candidate/" + value.CandidateId.ToLowerInvariant() + ":" + image.Role.ToLowerInvariant()) throw new IOException("Invalid image identity.");
        var names = new HashSet<string>(StringComparer.Ordinal) { "candidate.json", "SHA256SUMS" };
        foreach (var entry in value.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!names.Add(entry.Path) || entry.Size < 0 || !Regex.IsMatch(entry.Sha256 ?? "", "^[a-f0-9]{64}$")) throw new IOException("Invalid candidate entry.");
            var file = Within(directory, entry.Path);
            DeploymentBackup.SafeRoot(Path.GetDirectoryName(file)!);
            if (new FileInfo(file).LinkTarget is not null || new FileInfo(file).Length != entry.Size || await HashAsync(file, token) != entry.Sha256)
                throw new IOException("Candidate integrity failed.");
        }
        var count = 0;
        void Walk(string folder)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++count > 20000 || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid candidate content.");
                if (Directory.Exists(child)) Walk(child);
                else if (!names.Contains(Path.GetRelativePath(directory, child).Replace('\\', '/'))) throw new IOException("Unlisted candidate file.");
            }
        }
        Walk(directory);
        foreach (var required in new[] { "images.tar", "images.lock.tsv", "install.sh", "release/release.json", "templates/nginx.conf", "templates/Dockerfile", "templates/compose.yaml", "templates/svm.sh", "templates/operations.sh" })
            if (!names.Contains(required)) throw new IOException("Incomplete candidate.");
        using var release = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release/release.json"), token));
        if (release.RootElement.GetProperty("commit").GetString() != value.Commit || release.RootElement.GetProperty("dirty").GetBoolean() ||
            release.RootElement.GetProperty("runtimeMinimum").GetString() != "8.0.31" || release.RootElement.GetProperty("runtimeRollForward").GetString() != "LatestPatch")
            throw new IOException("Candidate requires a clean compatible release.");
        foreach (var item in release.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = "release/" + item.GetProperty("path").GetString();
            if (!value.Files.Any(x => x.Path == name && x.Sha256 == item.GetProperty("sha256").GetString() && x.Size == item.GetProperty("size").GetInt64()))
                throw new IOException("Release inventory differs from candidate.");
        }
        var rows = await File.ReadAllLinesAsync(Path.Combine(directory, "images.lock.tsv"), token);
        if (rows.Length != 4 || value.Images.Any(i => !rows.Contains($"{i.Role}\t{i.Reference}\t{i.Tag}\t{i.Id}\t{i.Platform}", StringComparer.Ordinal)))
            throw new IOException("Image lock differs from manifest.");
        return value;
    }
    internal static string Within(string root, string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 1024 || name.Any(char.IsControl) || name.Contains('\\') || name.StartsWith('/') ||
            name.Split('/').Any(p => p is "" or "." or "..")) throw new IOException("Invalid candidate path.");
        return Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
    }
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}
