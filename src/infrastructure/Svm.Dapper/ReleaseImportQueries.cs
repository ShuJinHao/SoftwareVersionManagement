using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Releases;

namespace Svm.Dapper;

internal sealed class ReleaseImportQueries(ReadQuerySession session, GitLabImportOptions options) : IReleaseImportQueries
{
    // The Application authorizer resolves the software and protects current personnel permissions first.
    public async Task<ReleaseSourceView> SourceAsync(Guid softwareId, CancellationToken token)
    {
        var stored = (await session.QueryAsync<SourceRow>("SELECT * FROM rel.source_bindings WHERE \"SoftwareId\"=@softwareId AND \"IsCurrent\"", new { softwareId }, token)).SingleOrDefault();
        var configured = options.Bindings.SingleOrDefault(b => b.SoftwareId == softwareId);
        if (stored is null) return new(softwareId, configured is null ? "Manual" : "GitLab", configured?.Enabled == true,
            configured?.BaseUri, configured?.ProjectId, configured?.PackageName, null, null, null, configured?.Id, ConfigurationAvailable: configured is not null);
        var binding = new GitLabBinding(stored.Id, softwareId, stored.SubjectId, stored.BaseUri, stored.ProjectId, stored.PackageName,
            stored.Managed ? stored.Enabled : configured?.Enabled == true, stored.ConnectionId, stored.SourceRevision);
        var blocking = await session.QueryAsync<Guid>("""
            SELECT w."Id" FROM rel.sync_works w WHERE w."BindingId"=@bindingId AND
              (w."Stage" NOT IN ('Completed','Conflict','Historical') OR EXISTS
                (SELECT 1 FROM rel.releases r WHERE r."Id"=w."ReleaseId" AND r."State"='Staging'))
            ORDER BY w."Id" LIMIT 20
            """, new { bindingId = stored.Id }, token);
        return new(softwareId, "GitLab", binding.Enabled && options.IsAvailable(binding), stored.BaseUri, stored.ProjectId, stored.PackageName,
            stored.LastSyncedAt, stored.LastErrorCode, stored.LatestWorkId, stored.Id, stored.ConnectionId, stored.Revision,
            options.IsAvailable(binding), stored.Managed, BlockingWorkIds: blocking.ToArray());
    }
    public async Task<ReleaseSyncWorkView> WorkAsync(Guid workId, CancellationToken token) =>
        (await session.QueryAsync<WorkRow>("SELECT \"Id\",\"SoftwareId\",\"Kind\",\"Tag\",\"Stage\",\"LastErrorCode\",\"ReleaseId\",\"CreatedAt\",\"CompletedAt\",\"Revision\" FROM rel.sync_works WHERE \"Id\"=@workId", new { workId }, token)).SingleOrDefault()?.View
        ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    private sealed class SourceRow
    {
        public Guid? ConnectionId { get; set; } public bool Managed { get; set; } public bool Enabled { get; set; }
        public long Revision { get; set; } public long SourceRevision { get; set; }
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
