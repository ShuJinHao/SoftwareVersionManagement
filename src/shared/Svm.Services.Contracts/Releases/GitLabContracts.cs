using System.Globalization;
using System.Text.Json;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.Services.Contracts.Releases;

// Contains no credentials. Binding identity is deployment configuration, never request input.
public sealed record GitLabBinding(Guid Id, Guid SoftwareId, Guid SubjectId, string BaseUri, long ProjectId, string PackageName, bool Enabled = true);
public sealed record GitLabImportOptions(IReadOnlyList<GitLabBinding> Bindings, int PollSeconds = 300, int PageSize = 20, int MaxPages = 10000, int LeaseSeconds = 120)
{
    public static GitLabImportOptions Disabled { get; } = new([]);
    public GitLabBinding Require(Guid softwareId) => Bindings.SingleOrDefault(b => b.SoftwareId == softwareId && b.Enabled)
        ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
}
public sealed record GitLabManifestV1(int SchemaVersion, long ProjectId, string Tag, string Version, string CommitSha,
    long PipelineId, long JobId, string PackageName, string PackageVersion, string FileName, long SizeBytes, string Sha256)
{
    public bool IsValid() => SchemaVersion == 1 && ProjectId > 0 && PipelineId > 0 && JobId > 0 &&
        GitLabVersions.TryParse(Tag, out var version) && Version == version && CommitSha is { Length: 40 or 64 } && CommitSha.All(char.IsAsciiHexDigit) &&
        GitLabVersions.Segment(PackageName) && GitLabVersions.Segment(PackageVersion) && GitLabVersions.Segment(FileName) &&
        FileName != "release-manifest.json" && SizeBytes > 0 && Sha256 is { Length: 64 } && Sha256.All(char.IsAsciiHexDigit);
}
public static class GitLabVersions
{
    public static bool TryParse(string? tag, out string version)
    {
        version = ""; if (string.IsNullOrEmpty(tag) || tag.Length > 34) return false;
        var value = tag.StartsWith('v') ? tag[1..] : tag;
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || p != n.ToString(CultureInfo.InvariantCulture)) || parts[0] == "0") return false;
        version = value; return true;
    }
    public static int Compare(string left, string right) => Version.Parse(left).CompareTo(Version.Parse(right));
    public static bool Segment(string? value) => value is { Length: > 0 and <= 200 } && value != "." && value != ".." && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
public sealed record GitLabReleaseFacts(GitLabManifestV1 Manifest, string ReleaseName, string PipelineUrl, DateTimeOffset ReleasedAt);
public sealed record GitLabScanPage(IReadOnlyList<string> Tags, int? NextPage);
public sealed record ReleaseSourceView(Guid SoftwareId, string Kind, bool Enabled, string? BaseUri, long? ProjectId,
    string? PackageName, DateTimeOffset? LastSyncedAt, string? LastErrorCode, Guid? LatestWorkId);
public sealed record ReleaseSyncWorkView(Guid Id, Guid SoftwareId, string Kind, string? Tag, string Stage,
    string? LastErrorCode, Guid? ReleaseId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, long Revision);
public sealed record GitLabWork(Guid Id, Guid BindingId, Guid SoftwareId, string Kind, string? Tag, string Stage,
    int Page, GitLabReleaseFacts? Candidate, bool InitialScan, Guid? ReleaseId, Guid? ReceiveToken,
    Guid? LeaseToken, long LeaseGeneration, DateTimeOffset? LeaseUntil);
public sealed record GitLabLease(Guid WorkId, Guid Token, long Generation);
public sealed record GitLabImportReceipt(GitLabWork Work, UploadReceipt? Upload);

// Network and streaming operations must be called outside a database transaction.
public interface IGitLabReleaseClient
{
    Task<GitLabScanPage> ListAsync(GitLabBinding binding, int page, CancellationToken token);
    Task<GitLabReleaseFacts?> VerifyAsync(GitLabBinding binding, string tag, CancellationToken token);
    Task<Stream> DownloadAsync(GitLabBinding binding, GitLabReleaseFacts release, CancellationToken token);
}
public interface IReleaseImports
{
    Task<bool> IsBoundAsync(Guid softwareId, CancellationToken token);
    Task<bool> AuthorizesPackageAsync(Guid releaseId, Guid subjectId, Guid softwareId, CancellationToken token);
    Task<ReleaseSourceView> SourceAsync(Guid softwareId, CancellationToken token);
    Task<ReleaseSyncWorkView> ViewAsync(Guid workId, CancellationToken token);
    Task<GitLabWork> WorkAsync(Guid workId, bool protect, CancellationToken token);
    Task<ReleaseSyncWorkView> ScheduleAsync(Guid softwareId, string? tag, bool automatic, CancellationToken token);
    Task<IReadOnlyList<Guid>> PendingAsync(Guid softwareId, CancellationToken token);
    Task<GitLabWork?> ClaimAsync(Guid workId, Guid leaseToken, CancellationToken token);
    Task<GitLabWork> ProtectAsync(GitLabLease lease, CancellationToken token);
    Task ScanAsync(GitLabLease lease, IReadOnlyList<GitLabReleaseFacts> releases, IReadOnlyList<string> tags, int? nextPage, CancellationToken token);
    Task<ReleaseView> RegisterAsync(GitLabLease lease, GitLabReleaseFacts facts, Guid packageId, CancellationToken token);
    Task SetReceiveAsync(GitLabLease lease, Guid receiveToken, CancellationToken token);
    Task ReceivedAsync(GitLabLease lease, CancellationToken token);
    Task DelayAsync(GitLabLease lease, string code, CancellationToken token);
    Task CompleteReleaseAsync(Guid releaseId, Guid packageId, CancellationToken token);
}
public interface IGitLabServiceIdentity { Guid? BindingId { get; } }
public interface IReleaseImportQueries
{
    Task<ReleaseSourceView> SourceAsync(Guid softwareId, CancellationToken token);
    Task<ReleaseSyncWorkView> WorkAsync(Guid workId, CancellationToken token);
}
public static class GitLabFacts
{
    public static string Encode(GitLabReleaseFacts facts) => JsonSerializer.Serialize(facts);
    public static GitLabReleaseFacts? Decode(string? value) => value is null ? null : JsonSerializer.Deserialize<GitLabReleaseFacts>(value);
    public static bool Same(GitLabReleaseFacts left, GitLabReleaseFacts right) => left.Manifest == right.Manifest;
}
