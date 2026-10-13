using Microsoft.EntityFrameworkCore;
using Svm.Core.Releases;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Tasks;

internal static class IntegrationMaterialModel
{
    internal static void Configure(ModelBuilder m)
    { var b = m.Entity<IntegrationMaterial>(); b.ToTable("integration_materials", "rel"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.HasIndex(x => new { x.ReleaseId, x.Revision }).IsUnique(); }
}
internal sealed class IntegrationMaterialRepository(SvmDbContext db, IUnitOfWork unit) : IIntegrationMaterialRepository
{
    public async Task<IReadOnlyList<IntegrationMaterial>> GetAsync(Guid release, int take, Guid? after, CancellationToken ct) => await db.Set<IntegrationMaterial>().FromSqlRaw("SELECT * FROM rel.integration_materials WHERE \"ReleaseId\"={0} AND ({1}::uuid IS NULL OR \"Revision\"<(SELECT \"Revision\" FROM rel.integration_materials WHERE \"Id\"={1} AND \"ReleaseId\"={0})) ORDER BY \"Revision\" DESC LIMIT {2}", release, (object?)after ?? DBNull.Value, take).AsNoTracking().ToListAsync(ct);
    public Task<IntegrationMaterial?> FindAsync(Guid id, CancellationToken ct) => db.Set<IntegrationMaterial>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<long> LatestRevisionAsync(Guid release, CancellationToken ct) => await db.Set<IntegrationMaterial>().Where(x => x.ReleaseId == release).MaxAsync(x => (long?)x.Revision, ct) ?? 0;
    public void Add(IntegrationMaterial m) { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); db.Add(m); }
}
