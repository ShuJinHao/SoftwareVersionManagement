using Microsoft.EntityFrameworkCore;
using Svm.Core.Releases;
using Svm.Core.Packages;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Packages;

internal sealed class ReleaseRepository(SvmDbContext context, IUnitOfWork unit) : IReleaseRepository
{
    public async Task<SoftwareRelease?> GetAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Require();
        var local = context.Set<SoftwareRelease>().Local.SingleOrDefault(x => x.Id.Value == id);
        if (local is not null) return local;
        var query = context.Set<SoftwareRelease>().FromSqlRaw("SELECT * FROM rel.releases WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? query.AsTracking() : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public Task<SoftwareRelease?> LatestAsync(Guid softwareId, CancellationToken token)
    { Require(); return context.Set<SoftwareRelease>().AsNoTracking().Where(x => x.SoftwareId == softwareId).OrderByDescending(x => x.Major).ThenByDescending(x => x.Minor).ThenByDescending(x => x.Patch).FirstOrDefaultAsync(token); }
    public void Add(SoftwareRelease release) { Require(); context.Add(release); }
    private void Require() { if (unit.CurrentOperationId is null || context.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
internal sealed class PackageRepository(SvmDbContext context, IUnitOfWork unit) : IPackageRepository
{
    public async Task<PackageAsset?> GetAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Require(); var local = context.Set<PackageAsset>().Local.SingleOrDefault(x => x.Id.Value == id); if (local is not null) return local;
        var q = context.Set<PackageAsset>().FromSqlRaw("SELECT * FROM pkg.packages WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? q.AsTracking() : q.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public async Task<PackageWork?> WorkAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Require(); var local = context.Set<PackageWork>().Local.SingleOrDefault(x => x.Id.Value == id); if (local is not null) return local;
        var q = context.Set<PackageWork>().FromSqlRaw("SELECT * FROM pkg.works WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? q.AsTracking() : q.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public async Task<PackageWork?> UploadAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Require();
        var q = context.Set<PackageWork>().FromSqlRaw("SELECT * FROM pkg.works WHERE \"PackageId\"={0} ORDER BY \"CreatedAt\" DESC,\"Id\" DESC" + (protect ? " FOR UPDATE" : ""), id);
        var rows = await (protect ? q.AsTracking() : q.AsNoTracking()).ToListAsync(token);
        return context.Set<PackageWork>().Local.Where(x => x.PackageId == id).Concat(rows).DistinctBy(x => x.Id).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id.Value).FirstOrDefault();
    }
    public async Task<IReadOnlyList<PackageReplica>> ReplicasAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Require();
        var q = context.Set<PackageReplica>().FromSqlRaw("SELECT * FROM pkg.replicas WHERE \"PackageId\"={0} ORDER BY \"NodeId\"" + (protect ? " FOR UPDATE" : ""), id);
        var rows = await (protect ? q.AsTracking() : q.AsNoTracking()).ToListAsync(token);
        return context.Set<PackageReplica>().Local.Where(x => x.PackageId == id).Concat(rows).DistinctBy(x => x.Id).ToArray();
    }
    public async Task<IReadOnlyList<Guid>> PendingAsync(DateTimeOffset now, int take, CancellationToken token) =>
        (await context.Set<PackageWork>().AsNoTracking().Where(x => x.Accepted && (x.State == "Pending" || x.State == "Running") &&
            (x.LeaseUntil == null || x.LeaseUntil <= now)).OrderBy(x => x.CreatedAt).Take(take).Select(x => x.Id).ToListAsync(token)).Select(x => x.Value).ToArray();
    public async Task<IReadOnlyList<Guid>> ReadyAsync(int take, Guid? after, CancellationToken token)
    {
        var q = context.Set<PackageAsset>().FromSqlRaw("SELECT * FROM pkg.packages WHERE \"State\"='Ready' AND NOT \"Disabled\" AND ({0}::uuid IS NULL OR \"Id\">{0}) ORDER BY \"Id\" LIMIT {1}", (object?)after ?? DBNull.Value, take);
        return (await q.AsNoTracking().Select(x => x.Id).ToListAsync(token)).Select(x => x.Value).ToArray();
    }
    public async Task<PackageDownload?> DownloadAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) { Require(); await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString()}, 71006))", token); }
        var q = context.Set<PackageDownload>().FromSqlRaw("SELECT * FROM pkg.download_sessions WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? q.AsTracking() : q.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public Task<bool> HasDispatchAsync(Guid workId, long dispatch, Guid eventId, CancellationToken token) =>
        context.Set<PackageDispatch>().AsNoTracking().AnyAsync(x => x.Id == eventId && x.WorkId == workId && x.Sequence == dispatch, token);
    public void Add(PackageAsset p, PackageWork w) { Require(); context.Add(p); context.Add(w); }
    public void Add(PackageReplica r) { Require(); context.Add(r); }
    public void Add(PackageWork w) { Require(); context.Add(w); }
    public void Add(PackageDownload d) { Require(); context.Add(d); }
    public void Add(PackageDispatch d) { Require(); context.Add(d); }
    public Task<PackageReceiveAttempt?> AttemptAsync(Guid id, CancellationToken token) => context.Set<PackageReceiveAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
    public void Add(PackageReceiveAttempt a) { Require(); context.Add(a); }
    private void Require() { if (unit.CurrentOperationId is null || context.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
