using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;

namespace Svm.Dapper;

internal sealed class ReleaseImportQueries(ReadQuerySession session, GitLabImportOptions options) : IReleaseImportQueries
{
    // The Application authorizer resolves the software and protects current personnel permissions first.
    public async Task<ReleaseSourceView> SourceAsync(Guid softwareId, CancellationToken token)
    {
        var stored = (await session.QueryAsync<SourceRow>("SELECT * FROM rel.source_bindings WHERE \"SoftwareId\"=@softwareId", new { softwareId }, token)).SingleOrDefault();
        var binding = options.Bindings.SingleOrDefault(b => b.SoftwareId == softwareId);
        return new(softwareId, binding is not null || stored is not null ? "GitLab" : "Manual", binding?.Enabled == true &&
            (stored is null || stored.Id == binding.Id && stored.SubjectId == binding.SubjectId && stored.BaseUri == binding.BaseUri && stored.ProjectId == binding.ProjectId && stored.PackageName == binding.PackageName),
            binding?.BaseUri ?? stored?.BaseUri, binding?.ProjectId ?? stored?.ProjectId, binding?.PackageName ?? stored?.PackageName,
            stored?.LastSyncedAt, stored?.LastErrorCode, stored?.LatestWorkId);
    }
    public async Task<ReleaseSyncWorkView> WorkAsync(Guid workId, CancellationToken token) =>
        (await session.QueryAsync<WorkRow>("SELECT \"Id\",\"SoftwareId\",\"Kind\",\"Tag\",\"Stage\",\"LastErrorCode\",\"ReleaseId\",\"CreatedAt\",\"CompletedAt\",\"Revision\" FROM rel.sync_works WHERE \"Id\"=@workId", new { workId }, token)).SingleOrDefault()?.View
        ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    private sealed class SourceRow
    {
        public Guid Id { get; set; } public Guid SubjectId { get; set; } public string BaseUri { get; set; } = ""; public long ProjectId { get; set; }
        public string PackageName { get; set; } = ""; public DateTimeOffset? LastSyncedAt { get; set; } public string? LastErrorCode { get; set; } public Guid? LatestWorkId { get; set; }
    }
    private sealed class WorkRow
    {
        public Guid Id { get; set; } public Guid SoftwareId { get; set; } public string Kind { get; set; } = ""; public string? Tag { get; set; }
        public string Stage { get; set; } = ""; public string? LastErrorCode { get; set; } public Guid? ReleaseId { get; set; }
        public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } public long Revision { get; set; }
        public ReleaseSyncWorkView View => new(Id, SoftwareId, Kind, Tag, Stage, LastErrorCode, ReleaseId, CreatedAt, CompletedAt, Revision);
    }
}
