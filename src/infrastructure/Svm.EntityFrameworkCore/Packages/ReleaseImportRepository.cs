using Microsoft.EntityFrameworkCore;
using Svm.Core.Releases;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Packages;

internal sealed class ReleaseImportRepository(SvmDbContext context, IUnitOfWork unit) : IReleaseImportRepository
{
    public async Task<ReleaseSourceBinding?> BindingAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Require();
        var local = context.Set<ReleaseSourceBinding>().Local.SingleOrDefault(x => x.SoftwareId == id);
        if (local is not null && protect) return local;
        var q = context.Set<ReleaseSourceBinding>().FromSqlRaw("SELECT * FROM rel.source_bindings WHERE \"SoftwareId\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? q.AsTracking() : q.AsNoTracking()).SingleOrDefaultAsync(token) ?? local;
    }
    public async Task<ReleaseSyncWork?> WorkAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Require();
        var local = context.Set<ReleaseSyncWork>().Local.SingleOrDefault(x => x.Id == id);
        if (local is not null && protect) return local;
        var q = context.Set<ReleaseSyncWork>().FromSqlRaw("SELECT * FROM rel.sync_works WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? q.AsTracking() : q.AsNoTracking()).SingleOrDefaultAsync(token) ?? local;
    }
    public async Task<ReleaseSyncWork?> FindAsync(Guid binding, string? tag, bool activeOnly, CancellationToken token)
    {
        Require();
        var local = context.Set<ReleaseSyncWork>().Local.FirstOrDefault(x => x.BindingId == binding && x.Tag == tag && (!activeOnly || x.Stage != "Completed" && x.Stage != "Conflict"));
        if (local is not null) return local;
        return await context.Set<ReleaseSyncWork>().FromSqlRaw("SELECT * FROM rel.sync_works WHERE \"BindingId\"={0} AND \"Tag\" IS NOT DISTINCT FROM {1}::text" +
            (activeOnly ? " AND \"Stage\" NOT IN ('Completed','Conflict')" : "") + " ORDER BY \"CreatedAt\" DESC LIMIT 1 FOR UPDATE", binding, (object?)tag ?? DBNull.Value).SingleOrDefaultAsync(token);
    }
    public async Task<IReadOnlyList<Guid>> PendingAsync(Guid software, DateTimeOffset now, CancellationToken token) =>
        await context.Set<ReleaseSyncWork>().AsNoTracking().Where(x => x.SoftwareId == software && x.Stage != "Completed" && x.Stage != "Conflict" && x.Stage != "Historical" &&
            x.NextAttemptAt <= now && (x.LeaseUntil == null || x.LeaseUntil <= now)).OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).Take(20).Select(x => x.Id).ToListAsync(token);
    public Task<SoftwareRelease?> VersionAsync(Guid software, int major, int minor, int patch, CancellationToken token) =>
        context.Set<SoftwareRelease>().AsNoTracking().SingleOrDefaultAsync(x => x.SoftwareId == software && x.Major == major && x.Minor == minor && x.Patch == patch, token);
    public Task<SoftwareRelease?> TagAsync(Guid binding, string tag, CancellationToken token) =>
        context.Set<SoftwareRelease>().SingleOrDefaultAsync(x => x.SourceBindingId == binding && x.SourceTag == tag, token);
    public void Add(ReleaseSourceBinding b) { Require(); context.Add(b); }
    public void Add(ReleaseSyncWork w) { Require(); context.Add(w); }
    private void Require() { if (unit.CurrentOperationId is null || context.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
