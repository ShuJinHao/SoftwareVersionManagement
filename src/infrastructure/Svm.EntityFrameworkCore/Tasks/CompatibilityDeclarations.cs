using Microsoft.EntityFrameworkCore;
using Svm.Core.Releases;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Tasks;

internal static class CompatibilityDeclarationModel
{
    internal static void Configure(ModelBuilder m)
    {
        var b = m.Entity<CompatibilityDeclaration>(); b.ToTable("compatibility_declarations", "rel"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.DatabaseRequirementsJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TargetReleaseId, x.DeclarationRevision }).IsUnique().HasDatabaseName("IX_compatibility_target_revision");
    }
}
internal sealed class CompatibilityDeclarationRepository(SvmDbContext db, IUnitOfWork unit) : ICompatibilityDeclarationRepository
{
    public Task<CompatibilityDeclaration?> LatestAsync(Guid target, CancellationToken ct) => db.Set<CompatibilityDeclaration>()
        .AsNoTracking().Where(x => x.TargetReleaseId == target).OrderByDescending(x => x.DeclarationRevision).FirstOrDefaultAsync(ct);
    public Task<CompatibilityDeclaration?> FindAsync(Guid id, CancellationToken ct) => db.Set<CompatibilityDeclaration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public void Add(CompatibilityDeclaration value)
    { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); db.Add(value); }
}
